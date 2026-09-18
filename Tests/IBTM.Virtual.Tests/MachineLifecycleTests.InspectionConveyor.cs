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
        await using var services = CreateInspectionServices(enableConveyor: true, enableNgTransfer: ng);
        var machine = services.GetRequiredService<MachineController>();
        var machineState = services.GetRequiredService<MachineState>();
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
        work.GetAssembly(HeatSinkSlot.HeatSink1).RecordBarcode("PCB-1");

        var inspected = false;
        var returned = false;
        var raises = 0;
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
                Interlocked.Increment(ref raises);
            }
            if (output == OutputIo.NgCarrierPickupDown && on && work.Station.CarrierPresent)
            {
                Assert.True(ng);
                Assert.True(work.Completed);
                Assert.True(work.Station.CarrierSeated);
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
                Assert.Equal(StationCylinderState.Up, work.Station.BackupPlate);
            }
            beltStarted.TrySetResult();
        };

        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => beltStarted.Task.IsCompleted, TimeSpan.FromSeconds(5)),
                $"State={conveyor.State}; alarm={machineState.AlarmMessage}; "
                    + string.Join(" | ", conveyorSteps));
            if (!ng && rearReady)
            {
                Assert.Equal(0, Volatile.Read(ref raises));
                Assert.True(discharged.Task.IsCompletedSuccessfully);
            }
            else
            {
                Assert.Equal(1, Volatile.Read(ref raises));
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
                        $"State={conveyor.State}; completed={work.Completed}; seated={work.Station.CarrierSeated}; "
                            + $"parked={work.IsTransferAtWaitingPosition()}; ready={conveyor.DownstreamReady}; "
                            + $"alarm={machineState.AlarmMessage}; "
                            + string.Join(" | ", conveyorSteps));
                }
            }
            Assert.Equal(MachineAlarm.None, machineState.Alarm);
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
        await using var services = CreateInspectionServices(enableConveyor: true);
        var machine = services.GetRequiredService<MachineController>();
        var machineState = services.GetRequiredService<MachineState>();
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
        fastening.GetAssembly(HeatSinkSlot.HeatSink2).RecordBarcode("S2-CARRIER");
        if (carrierWaitingAtS1)
        {
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            await placement.Station.SeatAsync(CancellationToken.None);
        }
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);

        var expectedMoves = carrierWaitingAtS1
            ? new[]
            {
                MainConveyorState.MovingBoltFasteningToInspection,
                MainConveyorState.MovingPcbPlacementToBoltFastening,
                MainConveyorState.ReceivingFrontCarrier,
            }
            : new[]
            {
                MainConveyorState.MovingBoltFasteningToInspection,
                MainConveyorState.ReceivingFrontCarrier,
                MainConveyorState.MovingPcbPlacementToBoltFastening,
            };
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
            Assert.True(fastening.Station.CarrierSeated);
            Assert.Equal(carrierWaitingAtS1, placement.Station.CarrierSeated);
            Assert.Equal(arrivingJob.Id, work.CurrentJob.Id);
            Assert.Equal("S2-CARRIER", work.GetAssembly(HeatSinkSlot.HeatSink2).PcbBarcode);
            Assert.Equal(new[] { true, false }, plateMovesBeforeInspection);
            Assert.Equal(expectedMoves, moves);
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
            Assert.True(work.Station.CarrierSeated);
            Assert.False(work.InspectionRequested);
            Assert.False(work.Completed);
        };

        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => work.Completed, TimeSpan.FromSeconds(8)),
                $"Alarm={machineState.AlarmMessage}; " + string.Join(" | ", trace));
            Assert.True(inspected);
            Assert.Equal(MachineAlarm.None, machineState.Alarm);
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
        await using var services = CreateInspectionServices(enableConveyor: false);
        var machine = services.GetRequiredService<MachineController>();
        var bolts = services.GetRequiredService<Recipe>().Pcb.GetBolts().ToArray();
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
        var run = station.RunAsync(bolts, stop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => work.IsTransferAtWaitingPosition(), TimeSpan.FromSeconds(2)));
            Assert.Equal(InspectionStationState.Waiting, station.GetState(bolts));
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
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

    private static ServiceProvider CreateInspectionServices(bool enableConveyor, bool enableNgTransfer = false)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.MainConveyor = enableConveyor;
        settings.Units.NgCarrierTransfer = enableNgTransfer;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<Recipe>());
        return services;
    }
}
