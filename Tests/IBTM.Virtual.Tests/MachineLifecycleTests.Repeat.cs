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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatReturnsFromLastEnabledNgUnitWithoutRunningTheNgConveyor(bool shuttleEnabled)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.NgCarrierTransfer = true;
        settings.Units.NgShuttle = shuttleEnabled;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var pickup = services.GetRequiredService<NgCarrierTransfer>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        if (!shuttleEnabled)
        {
            io.SetInput(InputIo.NgShuttleUp, false);
            io.SetInput(InputIo.NgShuttleDown, false);
            io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        }
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        state.RepeatEnabled = true;
        var shuttleOutputs = new ConcurrentQueue<bool>();
        var placedAndReleased = false;
        var pickedBackUp = false;
        var loweredWhileHolding = false;
        var openedAtShuttle = false;
        var ngConveyorRan = false;
        var stoppedForConfiguration = false;
        void CheckPickup()
        {
            if (gantry.IsAt(settings.NgCarrierTransfer.ShuttlePlacePosition)
                && pickup.Lift == NgTransferLiftState.Down
                && pickup.Gripper == NgTransferGripperState.Closed
                && pickup.CarrierDetected)
            {
                loweredWhileHolding = true;
            }

            if (gantry.IsAt(settings.NgCarrierTransfer.ShuttlePlacePosition)
                && io.GetInput(InputIo.NgShuttleCarrierDetected)
                && pickup.IsRaised
                && pickup.Gripper == NgTransferGripperState.Open
                && !pickup.CarrierDetected)
            {
                placedAndReleased = true;
            }

            if (placedAndReleased && pickup.CarrierDetected)
                pickedBackUp = true;
        }

        pickup.Changed += CheckPickup;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgCarrierGripperOpen && on
                && gantry.IsAt(settings.NgCarrierTransfer.ShuttlePlacePosition))
                openedAtShuttle = true;
            if (output == OutputIo.NgShuttleUp)
            {
                shuttleOutputs.Enqueue(on);
                if (!on && !stoppedForConfiguration)
                {
                    stoppedForConfiguration = true;
                    machine.Stop();
                }
            }
            if (output == OutputIo.NgConveyorRun && on)
                ngConveyorRan = true;
        };
        Assert.True(machine.CanStart, machine.StartBlock.ToString());
        await WaitUntilAsync(() => state.Display.CanStart);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var run = machine.StartAsync(timeout.Token);
        try
        {
            if (shuttleEnabled)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(stoppedForConfiguration);
                settings.Units.NgShuttle = false;
                Assert.Equal(StartBlockReason.RepeatReturnUnitDisabled, machine.StartBlock);
                Assert.False(machine.CanStart);
                await machine.StartAsync();
                Assert.Equal(new[] { false }, shuttleOutputs.ToArray());

                settings.Units.NgShuttle = true;
                await WaitUntilAsync(() => state.Display.CanStart);
                run = machine.StartAsync(timeout.Token);
            }

            Assert.True(await VirtualTest.WaitUntilAsync(
                () => state.Display.RepeatCycles >= 1 || state.IsError,
                TimeSpan.FromSeconds(10)),
                $"Phase={state.Display.RepeatPhase}, Alarm={state.AlarmDetail}");
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
            Assert.Equal(1, state.Display.RepeatCycles);
            Assert.True(loweredWhileHolding);
            Assert.Equal(shuttleEnabled, openedAtShuttle);
            Assert.Equal(shuttleEnabled, placedAndReleased);
            Assert.Equal(shuttleEnabled, pickedBackUp);
            Assert.False(ngConveyorRan);
            Assert.Equal(shuttleEnabled ? new[] { false, true } : [], shuttleOutputs.ToArray());
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            pickup.Changed -= CheckPickup;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProductionAllowsBothModesWhileRepeatRequiresManualAndSelectorChangeStops(
        bool repeat,
        bool manual)
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
        Task run = Task.CompletedTask;
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            state.RepeatEnabled = repeat;
            Assert.Equal(repeat, state.RepeatEnabled);
            io.SetInput(InputIo.PcbPlacementCarrierPresent, true);

            // The physical selector is ON in TEACHING/MANUAL, OFF in AUTO.
            if (repeat)
            {
                io.SetInput(InputIo.AutoMode, false);
                Assert.Equal(StartBlockReason.TeachingMode, machine.StartBlock);
                Assert.False(machine.CanStart);
                await machine.StartAsync();
                Assert.False(state.AutomaticRunning);
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            }

            io.SetInput(InputIo.AutoMode, manual);
            if (manual)
            {
                io.SetInput(InputIo.Door1Open, false);
                Assert.True(state.DoorInterlockReady);
            }
            Assert.True(machine.CanStart, machine.StartBlock.ToString());
            await WaitUntilAsync(() => state.Display.CanStart);
            run = machine.StartAsync();
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => io.GetOutput(OutputIo.MainConveyorRun), TimeSpan.FromSeconds(3)),
                state.AlarmDetail);
            Assert.True(state.AutomaticRunning);

            if (manual)
            {
                io.SetInput(InputIo.Door1Open, true);
                io.SetInput(InputIo.Door1Open, false);
                Assert.Equal(MachineAlarm.None, state.Alarm);
                Assert.True(state.AutomaticRunning);
                io.SetInput(InputIo.Door1Open, true);
            }
            io.SetInput(InputIo.AutoMode, !manual);
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.Equal(repeat ? StartBlockReason.TeachingMode : StartBlockReason.None, machine.StartBlock);
            await WaitUntilAsync(() => state.Display.CanStart == !repeat);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

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
        io.SetInput(InputIo.AutoMode, true);
        var returned = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.MainConveyorRun && on
                && !io.GetOutput(OutputIo.MainConveyorForward))
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
        io.SetInput(InputIo.AutoMode, true);
        var stopOutput = OutputIo.NgConveyorRun;
        void StopOnReverse(OutputIo output, bool on)
        {
            if (on && output == stopOutput
                && (output == OutputIo.NgConveyorRun
                    ? io.GetOutput(OutputIo.NgConveyorReverse)
                    : !io.GetOutput(OutputIo.MainConveyorForward)))
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
            if (output == OutputIo.PcbPlacementBackupPlateDown && !on)
            {
                forbidden.Enqueue(output);
            }

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
            if (output == OutputIo.MainConveyorRun && !io.GetOutput(OutputIo.MainConveyorForward))
            {
                Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
                Interlocked.Increment(ref mainReverse);
            }
        };

        state.RepeatEnabled = true;
        Assert.True(state.RepeatEnabled);
        io.SetInput(InputIo.AutoMode, true);
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
            Assert.Equal(0, visited.GetValueOrDefault(InputIo.MainConveyorEntryCarrierDetected));
            Assert.True(visited.GetValueOrDefault(InputIo.BoltFasteningBackupPlateUp) >= 2);
            Assert.True(visited.GetValueOrDefault(InputIo.InspectionBackupPlateUp) >= 2);
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
