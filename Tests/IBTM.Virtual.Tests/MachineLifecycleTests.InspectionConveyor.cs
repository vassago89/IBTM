using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InspectionParksThenDischargesOrRaisesBeforeOtherTransfers(bool ng, bool rearReady)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = true;
        settings.Units.NgCarrierTransfer = ng;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var recipe = services.GetRequiredService<Recipe>();
        PrepareCarrierTeaching(settings, recipe);
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var station = services.GetRequiredService<InspectionStation>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var pickup = services.GetRequiredService<NgCarrierTransfer>();
        services.GetRequiredService<VirtualCamera>().BoltsPresent = !ng;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, rearReady);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        // Start with an occupied, raised S3; the main sequence must lower it for inspection.
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        work.Assembly(HeatSinkSlot.HeatSink1).RecordBarcode("PCB-1");

        var inspected = false;
        var returned = false;
        var raises = new ConcurrentQueue<bool>();
        var conveyorSteps = new ConcurrentQueue<string>();
        conveyor.Trace += conveyorSteps.Enqueue;
        var beltStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discharged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        station.Trace += message =>
        {
            if (!inspected && message.Contains(": InspectingBolt ", StringComparison.Ordinal))
            {
                Assert.True(work.AtInspectionPosition);
                Assert.False(conveyor.RunCommandOn);
                Assert.Equal(MainConveyorState.WaitingForInspection, conveyor.State);
                inspected = true;
                // A carrier arriving after inspection was requested must wait for it to finish.
                io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            }
            if (inspected && message.Contains(": ReturningToNgPickup ", StringComparison.Ordinal))
            {
                Assert.False(work.Completed);
                Assert.False(conveyor.RunCommandOn);
                returned = true;
            }
        };
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.InspectionBackupPlateUp && on)
            {
                Assert.True(work.Completed);
                Assert.True(work.IsTransferAtWaitingPosition());
                raises.Enqueue(true);
            }
            if (output == OutputIo.NgCarrierPickupDown && on && work.CarrierPresent)
            {
                Assert.True(ng);
                Assert.True(work.Completed);
                Assert.True(work.CarrierSeated);
            }
            if (output != OutputIo.MainConveyorRun || !on)
                return;
            Assert.True(inspected);
            Assert.True(returned);
            Assert.True(work.Completed);
            if (conveyor.State == MainConveyorState.DischargingInspectionCarrier)
            {
                Assert.False(ng);
                Assert.True(work.IsTransferAtWaitingPosition());
                discharged.TrySetResult();
            }
            else
            {
                Assert.Equal(MainConveyorState.MovingPcbPlacementToBoltFastening, conveyor.State);
                Assert.Equal(StationCylinderState.Up, work.BackupPlate);
            }
            beltStarted.TrySetResult();
        };

        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => beltStarted.Task.IsCompleted, TimeSpan.FromSeconds(5)),
                $"State={conveyor.State}; alarm={services.GetRequiredService<MachineState>().AlarmMessage}; "
                    + string.Join(" | ", conveyorSteps));
            if (!ng && rearReady)
            {
                Assert.Empty(raises);
                Assert.True(discharged.Task.IsCompletedSuccessfully);
            }
            else
            {
                Assert.Single(raises);
                if (ng)
                {
                    Assert.True(await VirtualTest.WaitUntilAsync(
                        () => pickup.CarrierDetected && pickup.IsRaised,
                        TimeSpan.FromSeconds(3)));
                }
                else
                {
                    io.SetInput(InputIo.MainConveyorReadyFromRear, true);
                    Assert.True(await VirtualTest.WaitUntilAsync(
                        () => discharged.Task.IsCompleted, TimeSpan.FromSeconds(5)),
                        $"State={conveyor.State}; completed={work.Completed}; seated={work.CarrierSeated}; "
                            + $"parked={work.IsTransferAtWaitingPosition()}; ready={conveyor.DownstreamReady}; "
                            + $"alarm={services.GetRequiredService<MachineState>().AlarmMessage}; "
                            + string.Join(" | ", conveyorSteps));
                }
            }
            Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectionArrivalYieldsToStation2RefillAndFrontReceiving(bool carrierWaitingAtS1)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = true;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var recipe = services.GetRequiredService<Recipe>();
        PrepareCarrierTeaching(settings, recipe);
        var io = services.GetRequiredService<VirtualIoService>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var inspection = services.GetRequiredService<InspectionStation>();
        var work = services.GetRequiredService<InspectionWork>();
        var fastening = services.GetRequiredService<BoltFasteningWork>();
        var placement = services.GetRequiredService<PcbPlacementWork>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);
        await fastening.Station.SeatAsync(CancellationToken.None);
        var arrivingJob = fastening.CurrentJob;
        fastening.Assembly(HeatSinkSlot.HeatSink2).RecordBarcode("S2-CARRIER");
        if (carrierWaitingAtS1)
        {
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            await placement.Station.SeatAsync(CancellationToken.None);
        }
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);

        var moves = new ConcurrentQueue<MainConveyorState>();
        var trace = new ConcurrentQueue<string>();
        conveyor.Trace += trace.Enqueue;
        inspection.Trace += trace.Enqueue;
        var plateMovesBeforeInspection = new ConcurrentQueue<bool>();
        var inspected = false;
        inspection.Trace += message =>
        {
            if (inspected || !message.Contains(": InspectingBolt ", StringComparison.Ordinal))
                return;
            Assert.True(work.InspectionRequested);
            Assert.True(work.AtInspectionPosition);
            Assert.False(conveyor.RunCommandOn);
            Assert.True(fastening.CarrierSeated);
            Assert.Equal(carrierWaitingAtS1, placement.CarrierSeated);
            Assert.Equal(arrivingJob.Id, work.CurrentJob.Id);
            Assert.Equal("S2-CARRIER", work.Assembly(HeatSinkSlot.HeatSink2).PcbBarcode);
            Assert.Equal(new[] { true, false }, plateMovesBeforeInspection);
            Assert.Equal(carrierWaitingAtS1
                ? new[] { MainConveyorState.MovingBoltFasteningToInspection,
                    MainConveyorState.MovingPcbPlacementToBoltFastening, MainConveyorState.ReceivingFrontCarrier }
                : new[] { MainConveyorState.MovingBoltFasteningToInspection,
                    MainConveyorState.ReceivingFrontCarrier, MainConveyorState.MovingPcbPlacementToBoltFastening },
                moves);
            inspected = true;
        };
        io.OutputChanged += (output, on) =>
        {
            if (inspected)
                return;
            if (output == OutputIo.InspectionBackupPlateUp)
            {
                Assert.False(work.InspectionRequested);
                Assert.False(work.Completed);
                plateMovesBeforeInspection.Enqueue(on);
            }
            if (output != OutputIo.MainConveyorRun || !on)
                return;
            var state = conveyor.State;
            moves.Enqueue(state);
            if (state == MainConveyorState.MovingBoltFasteningToInspection)
                return;
            Assert.True(work.CarrierSeated);
            Assert.False(work.InspectionRequested);
            Assert.False(work.Completed);
        };

        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => work.Completed, TimeSpan.FromSeconds(8)),
                $"Alarm={services.GetRequiredService<MachineState>().AlarmMessage}; " + string.Join(" | ", trace));
            Assert.True(inspected);
            Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
        Assert.False(work.InspectionRequested);
    }

    [Fact]
    public async Task InspectionParksWhileIdleAndRejectsResultsIfConveyorStarts()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var recipe = services.GetRequiredService<Recipe>();
        PrepareCarrierTeaching(settings, recipe);
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var station = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await services.GetRequiredService<InspectionGantry>().MoveToAsync(new() { X = 12, Y = 7 }, 10_000);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beltForcedOn = false;
        station.Trace += message =>
        {
            if (message.Contains(": InspectingBolt ", StringComparison.Ordinal))
            {
                beltForcedOn = true;
                io.SetOutput(OutputIo.MainConveyorRun, true);
            }
            if (beltForcedOn && message.Contains("WaitingForInspectionPosition", StringComparison.Ordinal))
                interrupted.TrySetResult();
        };
        using var stop = new CancellationTokenSource();
        var run = station.RunAsync(recipe.Pcb.GetBolts().ToArray(), stop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => work.IsTransferAtWaitingPosition(), TimeSpan.FromSeconds(2)));
            Assert.Equal(InspectionStationState.Waiting, station.State(recipe.Pcb.GetBolts().ToArray()));
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            var assembly = work.Assembly(HeatSinkSlot.HeatSink1);
            assembly.RecordBarcode("PCB-1");
            await work.Station.PrepareToReceiveAsync(CancellationToken.None);
            await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(beltForcedOn);
            Assert.False(work.Completed);
            Assert.Empty(assembly.BoltPresenceResults);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }
}
