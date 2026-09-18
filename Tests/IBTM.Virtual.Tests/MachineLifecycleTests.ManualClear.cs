using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetPreservesSeatedCarrierAndResults(bool stoppedAutomatically)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<PcbPlacementWork>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
            await work.Station.SeatAsync(CancellationToken.None);
            var job = work.CurrentJob;
            var assembly = work.Assembly(HeatSinkSlot.HeatSink1);
            var completed = work.Completed;
            if (stoppedAutomatically)
            {
                var run = machine.StartAsync();
                try
                {
                    await WaitUntilAsync(() => state.AutomaticRunning);
                }
                finally
                {
                    machine.Stop();
                    await run.WaitAsync(TimeSpan.FromSeconds(3));
                }
                Assert.True(machine.RequiresManualClear);
            }

            state.SetError(MachineAlarm.MainConveyor);
            var plateWrites = 0;
            io.OutputChanged += (output, _) =>
            {
                if (output is OutputIo.PcbPlacementBackupPlateUp or OutputIo.PcbPlacementStopperUp)
                    plateWrites++;
            };
            Assert.True(machine.CanReset);
            await machine.ResetAsync();

            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Null(state.AlarmDetail);
            Assert.True(work.CarrierSeated);
            Assert.True(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
            Assert.Equal(0, plateWrites);
            Assert.Same(job, work.CurrentJob);
            Assert.Same(assembly, Assert.Single(work.Assemblies));
            Assert.Equal(completed, work.Completed);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(stoppedAutomatically, machine.RequiresManualClear);
            Assert.Equal(stoppedAutomatically
                ? StartBlockReason.ManualClearRequired
                : StartBlockReason.None, machine.StartBlock);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ResetClearsAlarmWithoutAcknowledgingHeldMaterialAfterAutomaticStop()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(machine.CanStart, machine.StartBlock.ToString());
        var run = machine.StartAsync();
        try
        {
            await WaitUntilAsync(() => state.AutomaticRunning);
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(StartBlockReason.ManualClearRequired, machine.StartBlock);
            Assert.True(machine.CanReset);

            var writes = 0;
            void CountOutput(OutputIo output, bool value)
            {
                writes++;
            }
            io.OutputChanged += CountOutput;
            await machine.StartAsync();
            io.OutputChanged -= CountOutput;
            Assert.Equal(0, writes);

            io.SetInput(InputIo.PcbPlacementPcbDetected, true);
            state.SetError(MachineAlarm.PcbPlacement);
            await machine.ResetAsync();
            Assert.True(machine.RequiresManualClear);
            Assert.False(state.IsError);
            Assert.Equal(StartBlockReason.ManualClearRequired, machine.StartBlock);
            io.SetInput(InputIo.PcbPlacementPcbDetected, false);
            Assert.True(machine.RequiresManualClear);
            // Disabling Supply must not hide the physical upstream carrier at RESET.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            await machine.ResetAsync();
            Assert.True(machine.RequiresManualClear);
            Assert.False(state.IsError);
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            await machine.ResetAsync();
            Assert.False(machine.RequiresManualClear);
            Assert.False(state.AutomaticRunning);
            Assert.True(machine.CanStart, state.AlarmDetail);

            using var nextStop = new CancellationTokenSource();
            run = machine.StartAsync(nextStop.Token);
            await WaitUntilAsync(() => state.AutomaticRunning);
            nextStop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(machine.RequiresManualClear);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }
}
