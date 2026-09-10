using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.BoltFastening;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{

    [Fact]
    [Trait("Category", "MachineFlow")]
    public async Task RepeatMainReturnStopsWhenNgPickupDrops()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.NgCarrierTransfer = true;
        settings.Units.NgShuttle = true;
        settings.Units.NgConveyor = true;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        state.RepeatEnabled = true;
        io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        io.SetInput(InputIo.AutoMode, false);
        var returned = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.MainConveyorRun && on
                && io.GetOutput(OutputIo.MainConveyorReverse))
            {
                returned = true;
                io.SetInput(InputIo.NgCarrierPickupUp, false);
                io.SetInput(InputIo.NgCarrierPickupDown, true);
            }
        };

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await machine.StartAsync(timeout.Token);
            Assert.True(returned);
            Assert.Equal(MachineAlarm.MainConveyor, state.Alarm);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.Equal(0, state.Display.RepeatCycles);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    [Trait("Category", "MachineFlow")]
    public async Task RepeatStopResumesThePendingReturnAndRejectsUnknownCarrierPosition()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.NgCarrierTransfer = true;
        settings.Units.NgShuttle = true;
        settings.Units.NgConveyor = true;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        state.RepeatEnabled = true;
        io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        io.SetInput(InputIo.AutoMode, false);
        var stopOutput = OutputIo.NgConveyorRun;
        void StopOnReverse(OutputIo output, bool on)
        {
            if (on && output == stopOutput
                && io.GetOutput(output == OutputIo.NgConveyorRun
                    ? OutputIo.NgConveyorReverse : OutputIo.MainConveyorReverse))
                machine.Stop();
        }

        io.OutputChanged += StopOnReverse;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await machine.StartAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => state.Display.RepeatPhase == RepeatPhase.ReturnToShuttle);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));

            // An old destination is not proof that the carrier is still there.
            io.SetInput(InputIo.NgConveyorPosition1Occupied, false);
            await machine.StartAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(MachineAlarm.NgConveyor, state.Alarm);
            Assert.Contains("known presence", state.AlarmMessage);
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
            await machine.ResetAsync();
            Assert.Equal(MachineAlarm.None, state.Alarm);

            stopOutput = OutputIo.MainConveyorRun;
            await machine.StartAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(8));
            await WaitUntilAsync(() => state.Display.RepeatPhase == RepeatPhase.ReturnToStart);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.True(io.GetInput(InputIo.InspectionCarrierPresent));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));

            io.OutputChanged -= StopOnReverse;
            var resumed = machine.StartAsync(timeout.Token);
            try
            {
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => state.Display.RepeatCycles >= 1 || state.IsError, TimeSpan.FromSeconds(8)));
                Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
                Assert.Equal(1, state.Display.RepeatCycles);
            }
            finally
            {
                machine.Stop();
                await resumed.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            io.OutputChanged -= StopOnReverse;
            await machine.ShutdownAsync();
        }
    }
    [Fact]
    [Trait("Category", "MachineFlow")]
    public async Task RepeatRunsAutoThroughDisabledStationsAndReturnsFromNgEndTwice()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.NgCarrierTransfer = true;
        settings.Units.NgShuttle = true;
        settings.Units.NgConveyor = true;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var visited = new ConcurrentDictionary<InputIo, int>();
        var forbidden = new ConcurrentQueue<OutputIo>();
        var ngReverse = 0;
        var mainReverse = 0;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        io.SetInput(InputIo.PcbPlacementCarrierPresent, true);
        io.InputChanged += (input, on) =>
        {
            if (on)
                visited.AddOrUpdate(input, 1, (_, count) => count + 1);
        };
        io.OutputChanged += (output, on) =>
        {
            if (!on)
                return;
            if (output is OutputIo.MainConveyorReadyToFront2
                or OutputIo.MainConveyorAvailableToRear
                or OutputIo.ShootBolt
                or OutputIo.ShootingFeederRunSignal)
                forbidden.Enqueue(output);
            if (output == OutputIo.NgConveyorRun && io.GetOutput(OutputIo.NgConveyorReverse))
            {
                Assert.True(io.GetInput(InputIo.NgShuttleDown));
                Interlocked.Increment(ref ngReverse);
            }
            if (output == OutputIo.MainConveyorRun && io.GetOutput(OutputIo.MainConveyorReverse))
            {
                Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
                Interlocked.Increment(ref mainReverse);
            }
        };

        state.RepeatEnabled = true;
        Assert.True(state.RepeatEnabled);
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.CanStart, machine.StartBlock.ToString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var run = machine.StartAsync(timeout.Token);
        try
        {
            Assert.True(
                await VirtualTest.WaitUntilAsync(
                    () => state.Display.RepeatCycles >= 2 || state.IsError,
                    TimeSpan.FromSeconds(22)),
                $"Repeat timed out. Phase={state.Display.RepeatPhase}, Main={state.MainConveyorState}, Alarm={state.AlarmMessage}");
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
            Assert.True(state.Display.RepeatCycles >= 2, state.AlarmDetail);
            Assert.True(ngReverse >= 2);
            Assert.True(mainReverse >= 2);
            Assert.True(visited.GetValueOrDefault(InputIo.NgConveyorPosition1Occupied) >= 2);
            Assert.True(visited.GetValueOrDefault(InputIo.InspectionCarrierPresent) >= 4);
            Assert.True(visited.GetValueOrDefault(InputIo.PcbPlacementCarrierPresent) >= 2);
            Assert.Empty(forbidden);
            Assert.Empty(services.GetRequiredService<PcbPlacementWork>().Assemblies);
            Assert.Empty(services.GetRequiredService<BoltFasteningWork>().Assemblies);
            Assert.Empty(services.GetRequiredService<InspectionWork>().Assemblies);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }

        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
        Assert.False(state.AutomaticRunning);
    }
}
