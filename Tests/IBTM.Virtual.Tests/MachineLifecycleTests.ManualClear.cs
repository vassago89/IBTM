using System;
using IBTM.Storage;
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
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
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
            var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
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
            }

            state.SetError(MachineAlarm.MainConveyor);
            var plateWrites = 0;
            io.OutputChanged += (output, _) =>
            {
                if (output is OutputIo.PcbPlacementBackupPlateUp or OutputIo.PcbPlacementStopperUp)
                    plateWrites++;
            };
            Assert.True(machine.IsResetAllowed);
            await machine.ResetAsync();

            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Null(state.AlarmDetail);
            Assert.True(work.Station.CarrierSeated);
            Assert.True(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
            Assert.Equal(0, plateWrites);
            Assert.Same(job, work.CurrentJob);
            Assert.Same(assembly, Assert.Single(work.Assemblies));
            Assert.Equal(completed, work.Completed);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.True(machine.IsStartAllowed);

            settings.Units.Inspection = false;
            settings.Units.MainConveyor = true;
            var restarted = machine.StartAsync();
            try
            {
                await VirtualTest.WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
                Assert.Equal(StationCylinderState.Down, work.Station.BackupPlate);
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
    public async Task MaterialInsideMachineDoesNotBlockStartOrAlarmReset()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgConveyor);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
        var run = machine.StartAsync();
        try
        {
            await WaitUntilAsync(() => state.AutomaticRunning);
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(StartBlockReason.None, machine.StartBlock);

            io.SetInput(InputIo.NgCarrierDetected, true);
            io.SetInputs(
                (InputIo.PcbPlacementHeatSink1Present, true),
                (InputIo.BoltFasteningHeatSink1Present, true),
                (InputIo.InspectionHeatSink1Present, true),
                (InputIo.NgConveyorPosition1Occupied, true),
                (InputIo.PcbPlacementPcbDetected, true),
                (InputIo.PcbPlacementVacuumDetected, true));
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.True(machine.IsStartAllowed);

            state.SetError(MachineAlarm.NgCarrierTransfer);
            await machine.ResetAsync();
            Assert.False(state.IsError);
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            // A waiting upstream carrier is normal material, not interrupted work.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            Assert.False(state.AutomaticRunning);
            Assert.True(machine.IsStartAllowed, state.AlarmDetail);

            using var nextStop = new CancellationTokenSource();
            run = machine.StartAsync(nextStop.Token);
            await WaitUntilAsync(() => state.AutomaticRunning);
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.True(io.GetInput(InputIo.PcbPlacementPcbDetected));
            Assert.True(io.GetInput(InputIo.PcbPlacementVacuumDetected));
            Assert.True(io.GetInput(InputIo.PcbPlacementHeatSink1Present));
            Assert.True(io.GetInput(InputIo.BoltFasteningHeatSink1Present));
            Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
            Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            nextStop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }
}
