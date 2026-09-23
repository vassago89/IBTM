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
using IBTM.Storage;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Fact]
    public async Task InspectionSeatsAtPickupThenUsesSeparateWaitingPosition()
    {
        await using var services = CreateInspectionServices(enableConveyor: true);
        services.GetRequiredService<UnitSettings>().BoltFastening = true;
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var inspection = services.GetRequiredService<InspectionStation>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        var transferSettings = services.GetRequiredService<NgCarrierTransferSettings>();
        transferSettings.WaitingPosition = new() { X = 15, Y = 35 };
        var waitingPosition = transferSettings.WaitingPosition;
        var seatedAtPickup = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.InspectionBackupPlateUp && on)
            {
                Assert.True(gantry.IsAt(transferSettings.GetCarrierPickupPosition()!));
                Assert.False(gantry.IsAt(waitingPosition));
                seatedAtPickup = true;
            }
        };
        var barcodePosition = recipe.CarrierImages.Single(image => image.IsBarcode
            && image.HeatSink == HeatSinkSlot.HeatSink1).Center;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await work.Station.PrepareToReceiveAsync(CancellationToken.None);
        await gantry.MoveToAsync(barcodePosition);
        Assert.False(gantry.IsAt(waitingPosition));
        var waitedAfterSeating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inspection.Trace += message =>
        {
            if (message.Contains(": WaitingForConveyor ", StringComparison.Ordinal))
                waitedAfterSeating.TrySetResult();
        };
        var barcodeCaptured = false;
        inspection.InspectionCaptured += (image, pcb, bolt) =>
        {
            if (bolt is null)
            {
                Assert.True(gantry.IsAt(barcodePosition));
                barcodeCaptured = true;
            }
        };
        work.RequestCarrierSeating(work.CurrentJob);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = inspection.RunAsync(recipe.Pcb.BoltPoints.ToArray(), stop.Token);
        try
        {
            await waitedAfterSeating.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StationCylinderState.Up, work.Station.BackupPlate);
            Assert.True(seatedAtPickup);
            Assert.True(gantry.IsAt(waitingPosition));
            Assert.False(barcodeCaptured);
            Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
            Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));

            await work.Station.PrepareToReceiveAsync(stop.Token);
            work.RequestInspection(work.CurrentJob);
            Assert.True(await VirtualTest.WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(2)));
            Assert.True(barcodeCaptured);
            Assert.True(gantry.IsAt(waitingPosition));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InspectionSeatingCancelledDuringPickupTravelDoesNotRaisePlate()
    {
        await using var services = CreateInspectionServices(enableConveyor: true);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var station = services.GetRequiredService<InspectionStation>();
        var transfer = services.GetRequiredService<InspectionStation>();
        var settings = services.GetRequiredService<InspectionGantrySettings>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await work.Station.PrepareToReceiveAsync(CancellationToken.None);
        await transfer.MoveToAsync(new() { X = 100, Y = 100 }, 10_000);
        settings.Motion.HorizontalSpeed = 1;
        var raised = false;
        io.OutputChanged += (output, on) => raised |= output == OutputIo.InspectionBackupPlateUp && on;
        using var stop = new CancellationTokenSource();
        work.RequestCarrierSeating(work.CurrentJob);
        var run = station.RunAsync([], stop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => transfer.Feedback.IsMoving, TimeSpan.FromSeconds(2)));
            Assert.Equal(InspectionStationState.SeatingCarrier, station.GetState());
            Assert.False(raised);
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(raised);
            Assert.False(work.CarrierSeatingRequested);
            Assert.Equal(StationCylinderState.Down, work.Station.BackupPlate);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectionStartRepeatsEveryPointFromTheFirstPcb(bool stopAfterCompletion)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.PcbHistory.Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"PCB-inspection-{Guid.NewGuid():N}");
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [
            new() { Number = 4, HeatSink = HeatSinkSlot.HeatSink2, X = 30, Y = 20 },
            new() { Number = 3, HeatSink = HeatSinkSlot.HeatSink1, X = 10, Y = 20 },
            new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink2, X = 30, Y = 10 },
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink1, X = 10, Y = 10 },
        ];
        TeachInspectionFovs(settings, recipe);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var inspection = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInputs((InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, true));
        await work.Station.PrepareToReceiveAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordPcbBolt(1, new(false, 0.5, Error: "Existing fastening NG"));
        var number = assembly.PcbNumber;
        var captures = new ConcurrentQueue<(HeatSinkSlot Pcb, int? Bolt)>();
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstRun = true;
        inspection.InspectionCaptured += (image, pcb, bolt) =>
        {
            captures.Enqueue((pcb, bolt));
            if (firstRun && !stopAfterCompletion && pcb == HeatSinkSlot.HeatSink2 && bolt is null)
                firstStop.Cancel();
        };
        var run = machine.StartAsync(firstStop.Token);
        if (stopAfterCompletion)
        {
            Assert.True(await VirtualTest.WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(3)));
            firstStop.Cancel();
        }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(stopAfterCompletion, work.Completed);
        Assert.Equal(2, assembly.BoltPresenceResults.Count);
        Assert.Equal(MachineAlarm.None, state.Alarm);

        firstRun = false;
        captures.Clear();
        using var secondStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        run = machine.StartAsync(secondStop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => captures.Count == 6 && work.Completed, TimeSpan.FromSeconds(3)), state.AlarmDetail);
            Assert.Equal(new (HeatSinkSlot, int?)[] {
                (HeatSinkSlot.HeatSink1, null), (HeatSinkSlot.HeatSink1, 1), (HeatSinkSlot.HeatSink1, 3),
                (HeatSinkSlot.HeatSink2, null), (HeatSinkSlot.HeatSink2, 2), (HeatSinkSlot.HeatSink2, 4),
            }, captures);
            Assert.Same(assembly, work.GetAssembly(HeatSinkSlot.HeatSink1));
            Assert.Equal(number, assembly.PcbNumber);
            Assert.Equal("Existing fastening NG", assembly.PcbBoltResults[1].Error);
            Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            secondStop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InspectionParksThenDischargesOrRaisesBeforeOtherTransfers(bool ng, bool rearReady)
    {
        await using var services = CreateInspectionServices(enableConveyor: true);
        var machine = services.GetRequiredService<MachineController>();
        var machineState = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var station = services.GetRequiredService<InspectionStation>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var pickup = services.GetRequiredService<InspectionStation>();
        var transferSettings = services.GetRequiredService<NgCarrierTransferSettings>();
        transferSettings.WaitingPosition = new() { X = 15, Y = 35 };
        var waitingPosition = transferSettings.WaitingPosition;
        services.GetRequiredService<VirtualCamera>().BoltsPresent = !ng;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, rearReady);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        // Start with an occupied, raised S3; the main sequence must lower it for inspection.
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        var inspectedAssembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        inspectedAssembly.PcbBarcode = "PCB-1";

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
            if (inspected && message.Contains(": CompletingInspection ", StringComparison.Ordinal))
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
                Assert.Equal(InspectionStationState.SeatingCarrier, station.GetState());
                Assert.Equal(MainConveyorState.WaitingForInspectionTransfer, conveyor.State);
                Assert.False(conveyor.RunCommandOn);
                Assert.True(pickup.IsAt(services.GetRequiredService<NgCarrierTransferSettings>().GetCarrierPickupPosition()!));
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
            // NG pickup can already have removed the completed carrier from S3.
            Assert.Equal(ng ? AssemblyResult.Ng : AssemblyResult.Ok, inspectedAssembly.InspectionResult);
            if (!ng)
                Assert.True(work.Completed);
            if (conveyor.State == MainConveyorState.DischargingInspectionCarrier)
            {
                Assert.False(ng);
                Assert.True(work.IsTransferAtWaitingPosition());
                Assert.True(pickup.IsAt(waitingPosition));
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
                        () => io.GetInput(InputIo.NgCarrierDetected) && pickup.IsRaised,
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
        var arrivingAssembly = fastening.GetAssembly(HeatSinkSlot.HeatSink2);
        arrivingAssembly.PcbBarcode = "S2-CARRIER";
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
            Assert.Same(arrivingAssembly, work.GetAssembly(HeatSinkSlot.HeatSink2));
            Assert.Equal("PCB-2", arrivingAssembly.PcbBarcode);
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
                if (on)
                {
                    Assert.Equal(InspectionStationState.SeatingCarrier, inspection.GetState());
                    Assert.Equal(MainConveyorState.WaitingForInspectionTransfer, conveyor.State);
                    Assert.False(conveyor.RunCommandOn);
                    Assert.True(services.GetRequiredService<InspectionStation>()
                        .IsAt(services.GetRequiredService<NgCarrierTransferSettings>().GetCarrierPickupPosition()!));
                }
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
        var bolts = services.GetRequiredService<RecipeManager>().Current.Pcb.BoltPoints.ToArray();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var station = services.GetRequiredService<InspectionStation>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var waitingPosition = services.GetRequiredService<NgCarrierTransferSettings>().WaitingPosition!;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 12, Y = 7 }, 10_000);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beltForcedOn = false;
        station.Trace += message =>
        {
            if (message.Contains(": InspectingBolt ", StringComparison.Ordinal))
            {
                beltForcedOn = true;
                io.SetOutput(OutputIo.MainConveyorRun, true);
            }
            if (beltForcedOn && message.Contains(": Waiting ", StringComparison.Ordinal))
                interrupted.TrySetResult();
        };
        using var stop = new CancellationTokenSource();
        var run = station.RunAsync(bolts, stop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => work.IsTransferAtWaitingPosition(), TimeSpan.FromSeconds(2)));
            Assert.True(gantry.IsAt(waitingPosition));
            Assert.Equal(InspectionStationState.Waiting, station.GetState());
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
            assembly.PcbBarcode = "PCB-1";
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

    private static ServiceProvider CreateInspectionServices(bool enableConveyor)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.MainConveyor = enableConveyor;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        return services;
    }
}
