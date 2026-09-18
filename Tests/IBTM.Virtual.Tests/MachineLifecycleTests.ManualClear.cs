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
    public async Task ResetPreservesSeatedCarrierAndAllowsStartingItsTransfer(bool stoppedAutomatically)
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
                Assert.False(machine.RequiresManualClear);
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
            Assert.False(machine.RequiresManualClear);
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.True(machine.CanStart);

            settings.Units.NgCarrierTransfer = false;
            settings.Units.MainConveyor = true;
            var restarted = machine.StartAsync();
            try
            {
                await VirtualTest.WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
                Assert.Equal(StationCylinderState.Down, work.BackupPlate);
                Assert.True(state.AutomaticRunning);
            }
            finally
            {
                machine.Stop();
                await restarted.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task NgPickupHeldCarrierBlocksStartFromCurrentInputWithoutBlockingAlarmReset()
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
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.False(machine.RequiresManualClear);

            io.SetInput(InputIo.NgCarrierDetected, true);
            Assert.Equal(StartBlockReason.NgCarrierHeld, machine.StartBlock);

            var writes = 0;
            void CountOutput(OutputIo output, bool value)
            {
                writes++;
            }
            io.OutputChanged += CountOutput;
            await machine.StartAsync();
            io.OutputChanged -= CountOutput;
            Assert.Equal(0, writes);

            state.SetError(MachineAlarm.NgCarrierTransfer);
            await machine.ResetAsync();
            Assert.False(machine.RequiresManualClear);
            Assert.False(state.IsError);
            Assert.Equal(StartBlockReason.NgCarrierHeld, machine.StartBlock);
            io.SetInput(InputIo.NgCarrierDetected, false);
            Assert.False(machine.RequiresManualClear);
            // A waiting upstream carrier is normal material, not interrupted work.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            Assert.True(machine.CanStart, machine.StartBlock.ToString());
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            Assert.False(machine.RequiresManualClear);
            Assert.False(state.AutomaticRunning);
            Assert.True(machine.CanStart, state.AlarmDetail);

            using var nextStop = new CancellationTokenSource();
            run = machine.StartAsync(nextStop.Token);
            await WaitUntilAsync(() => state.AutomaticRunning);
            nextStop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(machine.RequiresManualClear);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }
}
