using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.Storage;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTestSupport;

namespace IBTM.Virtual.Tests;

public sealed class NgHandoffTests
{
    [Fact]
    public async Task InspectionReturnsOnceBeforeWaitingEvenWithSmallPositionError()
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var transfer = system.Inspection;
        await motion.AdjustAxisAsync(MotionAxis.X, 0.04, 1_000);
        var moves = 0;
        motion.MovingChanged += moving =>
        {
            if (moving)
                Interlocked.Increment(ref moves);
        };
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transfer.StepChanged += () =>
        {
            if (transfer.Step is InspectionStationState.Waiting)
                waiting.TrySetResult();
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var run = transfer.RunAsync(stop.Token);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(0, motion.Position.X);
            Assert.False(motion.IsMoving);
            system.Io.SetOutput(OutputIo.MainConveyorRun, true);
            await Task.Delay(50);
            Assert.Equal(1, Volatile.Read(ref moves));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task PickupRaisesBeforeMovingToItsTargetAndGripping()
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var transfer = system.Inspection;
        await motion.AdjustAxisAsync(MotionAxis.X, 0.04, 1_000);

        await transfer.MoveToCarrierAsync(NgTransferDestination.Station, CancellationToken.None);
        Assert.Equal(0, motion.Position.X);

        await transfer.SetLiftUpAsync(false);
        await Assert.ThrowsAsync<MotionInterlockException>(() =>
            transfer.MoveToCarrierAsync(NgTransferDestination.Station, CancellationToken.None));

        // A new pickup starts by raising, then moving to its source.
        await transfer.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None);
        Assert.True(transfer.IsTransferPending);
        Assert.True(transfer.IsRaised);
        Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);
    }

    [Fact]
    public async Task CancelledLiftDoesNotReportLostGripOrChangeTransferOwnership()
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var transfer = system.Inspection;
        await transfer.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None);
        system.Io.SetInputs(
            (InputIo.NgCarrierGripperClosed, false), (InputIo.NgCarrierGripperOpen, false));
        var commanded = false;
        system.Io.OutputChanged += (output, value) => commanded = true;
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.SetLiftUpAsync(true, stop.Token));

        Assert.False(commanded);
        Assert.True(transfer.IsTransferPending);
        await Assert.ThrowsAsync<MotionInterlockException>(() => transfer.SetLiftUpAsync(true));
    }

    [Fact]
    public async Task InspectionRepeatEndRequiresActiveHoldingStepAndWakesOnStepChange()
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var transfer = system.Inspection;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await transfer.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, stop.Token);
        SetCarrier(system.Io, InputIo.InspectionHeatSink1Present, false);
        await transfer.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PlacingCarrier, stop.Token,
            repeat: true);
        Assert.True(transfer.IsTransferPending);
        Assert.True(transfer.IsRaised);
        Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);
        Assert.Null(transfer.Step);

        var end = transfer.WaitForRepeatEndAsync(stop.Token);
        var run = Task.CompletedTask;
        try
        {
            // The same physical position while idle is not an active Repeat completion.
            Assert.False(end.IsCompleted);
            run = transfer.RunAsync(stop.Token, repeat: true);
            // Entering the holding step changes no sensor; StepChanged must wake the waiter.
            await end.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(InspectionStationState.HoldingAtDestination, transfer.Step);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
            try
            {
                await end;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }

        Assert.Null(transfer.Step);
        using var next = new CancellationTokenSource();
        var nextEnd = transfer.WaitForRepeatEndAsync(next.Token);
        try
        {
            Assert.False(nextEnd.IsCompleted);
        }
        finally
        {
            next.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextEnd);
        }
    }

    [Fact]
    public async Task DisabledNgConveyorBlocksProductionHandoffButAllowsInspectionRepeat()
    {
        var units = new UnitSettings { MainConveyor = false, NgConveyor = false };
        var system = await CreateAsync(units);
        using var motion = system.Motion;
        var transfer = system.Inspection;
        transfer.Station.GetAssembly(HeatSinkSlot.HeatSink1)
            .RecordBoltPresence(VirtualTestSupport.BoltId(1), false);
        transfer.Station.Complete(transfer.Station.CurrentJob);

        Assert.False(system.Conveyor.IsReceiveAllowed);
        Assert.Equal(InspectionStationState.WaitingForDestination, transfer.GetNextStep());
        Assert.Equal(InspectionStationState.PickingCarrier, transfer.GetNextStep(repeat: true));
        Assert.False(transfer.IsTransferPending);

        units.NgConveyor = true;
        Assert.True(system.Conveyor.IsReceiveAllowed);
        Assert.Equal(InspectionStationState.PickingCarrier, transfer.GetNextStep());
    }

    [Fact]
    public async Task DisabledInspectionKeepsPendingTransferAndPickupClearanceInterlocks()
    {
        var units = new UnitSettings { MainConveyor = false };
        var system = await CreateAsync(units);
        using var motion = system.Motion;
        var transfer = system.Inspection;
        await transfer.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None);
        SetCarrier(system.Io, InputIo.InspectionHeatSink1Present, false);
        units.Inspection = false;

        Assert.True(transfer.IsTransferPending);
        Assert.False(transfer.IsReceiveAllowed);
        Assert.False(transfer.IsTransferAtWaitingPosition);

        await transfer.SetGripperOpenAsync(true);
        await transfer.SetLiftUpAsync(false);
        Assert.False(transfer.IsTransferAtWaitingPosition);
        await transfer.SetLiftUpAsync(true);
        Assert.True(transfer.IsReceiveAllowed);
        Assert.True(transfer.IsTransferAtWaitingPosition);
    }

    [Fact]
    public async Task NgRepeatEndWakesWhenOnlyTransferOwnershipClears()
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var transfer = system.Inspection;
        await transfer.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None);
        // Feedback alone must not clear an unfinished commanded handoff.
        system.Io.AutoResponseEnabled = false;
        system.Io.SetInputs(
            (InputIo.NgConveyorPosition1Occupied, true),
            (InputIo.NgCarrierGripperClosed, false),
            (InputIo.NgCarrierGripperOpen, true));
        Assert.True(transfer.IsTransferPending);
        using var stop = new CancellationTokenSource();
        var run = system.Conveyor.RunAsync(stop.Token, repeat: true);
        var end = system.Conveyor.WaitForRepeatEndAsync(stop.Token);
        try
        {
            Assert.Equal(NgConveyorState.ReadyToEject, system.Conveyor.Step);
            Assert.False(end.IsCompleted);
            // Inputs and the conveyor step stay unchanged; only command ownership changes.
            await transfer.SetGripperOpenAsync(true);
            Assert.True(transfer.IsClear);
            await end.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
            try
            {
                await end;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyLoweredShuttleRisesBeforeInspectionPickup(bool pickupNeedsPreparation)
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var io = system.Io;
        await system.Conveyor.SetShuttleDownAsync(true);
        Assert.False(system.Io.GetInput(InputIo.NgShuttleCarrierDetected));
        if (pickupNeedsPreparation)
        {
            await system.Inspection.SetLiftUpAsync(false);
            await system.Inspection.SetGripperOpenAsync(false);
        }
        var picked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgCarrierGripperClose && on)
            {
                Assert.Equal(StationCylinderState.Up, system.Conveyor.ShuttleLift);
                picked.TrySetResult();
            }
            if (output == OutputIo.NgShuttleDown && !on)
            {
                Assert.False(system.Inspection.IsTransferPending);
                Assert.True(system.Inspection.IsRaised);
                Assert.Equal(NgTransferGripperState.Open, system.Inspection.Gripper);
            }
        };
        using var stop = new CancellationTokenSource();
        var inspection = system.Inspection.RunAsync(stop.Token);
        system.Inspection.Station.Complete(system.Inspection.Station.CurrentJob);
        var conveyor = system.Conveyor.RunAsync(stop.Token);
        try
        {
            await picked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(system.Inspection.IsTransferPending);
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(inspection, conveyor).WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferIgnoresPickupDetectionWhileDisplayStillUpdates(bool detected)
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var io = system.Io;
        var transfer = system.Inspection;
        var signals = new IoSignals([new NgCarrierTransferHardwareSettings()], io);
        var workChanges = 0;
        system.Inspection.Changed += () => workChanges++;
        foreach (var value in new[] { !detected, detected })
        {
            io.SetInput(InputIo.NgCarrierDetected, value);
            Assert.Equal(value, signals.Inputs[InputIo.NgCarrierDetected].IsOn);
            Assert.False(transfer.IsTransferPending);
        }
        Assert.Equal(0, workChanges);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await transfer.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, stop.Token);
        Assert.True(transfer.IsTransferPending);
        Assert.True(transfer.IsRaised);
        Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);
        SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        Assert.Equal(InspectionStationState.PlacingCarrier,
            transfer.GetNextStep());

        var changedDuringTravel = false;
        motion.PositionChanged += (x, y, z) =>
        {
            changedDuringTravel = true;
            io.SetInput(InputIo.NgCarrierDetected, !io.GetInput(InputIo.NgCarrierDetected));
        };
        await transfer.ExecuteTransferAsync(NgTransferDestination.Shuttle,
            InspectionStationState.PlacingCarrier, stop.Token, repeat: true);
        Assert.True(changedDuringTravel);
        Assert.True(transfer.IsRaised);
        Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);
        foreach (var value in new[] { false, true })
        {
            io.SetInput(InputIo.NgCarrierDetected, value);
            Assert.True(transfer.IsTransferPending);
            Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);
        }

        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        await transfer.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PlacingCarrier, stop.Token);
        Assert.False(transfer.IsTransferPending);
        Assert.True(transfer.IsClear);
        Assert.Equal(InspectionStationState.WaitingForShuttleDown, transfer.GetNextStep());
        Assert.True(signals.Inputs[InputIo.NgCarrierDetected].IsOn);
    }

    [Fact]
    public async Task ReleasedCarrierMustRemainDetectedBeforePickupRises()
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var transfer = system.Inspection;
        var io = system.Io;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await transfer.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, stop.Token);
        SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        await transfer.ExecuteTransferAsync(NgTransferDestination.Shuttle,
            InspectionStationState.PlacingCarrier, stop.Token, repeat: true);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        var releaseRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.NgCarrierGripperClose || on)
                return;
            io.SetInput(InputIo.NgShuttleCarrierDetected, false);
            releaseRequested.TrySetResult();
        };
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        try
        {
            await Task.Factory.StartNew(async () =>
            {
                var placing = transfer.ExecuteTransferAsync(
                    NgTransferDestination.Shuttle, InspectionStationState.PlacingCarrier, stop.Token);
                try
                {
                    await releaseRequested.Task.WaitAsync(stop.Token);
                    io.SetInput(InputIo.NgShuttleCarrierDetected, true);
                    io.SetInput(InputIo.NgShuttleCarrierDetected, false);
                    await Task.Yield();
                    Assert.True(io.GetOutput(OutputIo.NgCarrierPickupDown));
                    Assert.False(placing.IsCompleted);

                    io.SetInput(InputIo.NgShuttleCarrierDetected, true);
                    await placing;
                    Assert.True(transfer.IsRaised);
                }
                finally
                {
                    stop.Cancel();
                    await ((Task)placing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing
                        | ConfigureAwaitOptions.ContinueOnCapturedContext);
                }
            }, CancellationToken.None, TaskCreationOptions.None, scheduler.ExclusiveScheduler).Unwrap();
        }
        finally
        {
            scheduler.Complete();
            await scheduler.Completion;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectionReturnsOnlyAfterShuttleDown(bool restartWhileWaiting)
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var io = system.Io;
        var transfer = system.Inspection;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await transfer.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, stop.Token);
        SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        await transfer.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PlacingCarrier, stop.Token);
        Assert.True(transfer.IsClear);
        Assert.Equal(NgTransferGripperState.Open, transfer.Gripper);
        Assert.Equal(InspectionStationState.WaitingForShuttleDown, transfer.GetNextStep());
        Assert.Equal(NgConveyorState.LoweringShuttle, system.Conveyor.GetNextStep(system.Io.GetOutput(OutputIo.NgConveyorRun)));

        // Delay the real feedback after the shuttle receives its DOWN command.
        io.AutoResponseEnabled = false;
        var movedBeforeDown = false;
        motion.PositionChanged += (x, y, z) =>
            movedBeforeDown |= system.Conveyor.ShuttleLift != StationCylinderState.Down;
        using var firstRun = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var inspection = transfer.RunAsync(firstRun.Token);
        if (restartWhileWaiting)
        {
            firstRun.Cancel();
            await inspection.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(InspectionStationState.WaitingForShuttleDown, transfer.GetNextStep());
            inspection = transfer.RunAsync(stop.Token);
        }
        var conveyor = system.Conveyor.RunAsync(stop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => io.GetOutput(OutputIo.NgShuttleDown), TimeSpan.FromSeconds(1)));
            Assert.Equal((10d, 10d, 0d), motion.Position);
            Assert.Equal(InspectionStationState.WaitingForShuttleDown, transfer.GetNextStep());
            io.SetInput(InputIo.NgShuttleUp, false);
            Assert.Equal(InspectionStationState.WaitingForShuttleDown, transfer.GetNextStep());
            await Assert.ThrowsAsync<MotionInterlockException>(
                () => transfer.MoveToAsync(new(), cancellationToken: stop.Token));
            io.SetInputs((InputIo.NgShuttleUp, true), (InputIo.NgShuttleDown, true));
            Assert.Equal(InspectionStationState.WaitingForShuttleDown, transfer.GetNextStep());
            io.SetInput(InputIo.NgShuttleUp, false);
            Assert.True(await WaitUntilAsync(
                () => MotionServiceBase.IsAt(transfer.Motion.Feedback, new()) && transfer.GetNextStep() == InspectionStationState.Waiting,
                TimeSpan.FromSeconds(1)));
            Assert.False(movedBeforeDown);
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(inspection, conveyor).WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShuttleWaitsForCommandedReleaseAndRaisedOpenPickup(bool repeat)
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var io = system.Io;
        var transfer = system.Inspection;
        io.SetInput(InputIo.NgCarrierDetected, true);
        Assert.False(transfer.IsTransferPending); // Presence alone never establishes pickup ownership.
        await transfer.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None);
        await transfer.MoveToAsync(new() { X = 10, Y = 10 });
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.True(transfer.IsTransferPending);
        Assert.True(transfer.IsRaised);
        Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);

        var lowering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.NgShuttleDown || !on)
                return;
            Assert.False(transfer.IsTransferPending);
            Assert.True(transfer.IsRaised);
            Assert.Equal(NgTransferGripperState.Open, transfer.Gripper);
            lowering.TrySetResult();
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = repeat ? system.Conveyor.RunRepeatAsync(stop.Token)
            : system.Conveyor.RunAsync(stop.Token);
        try
        {
            Assert.Equal(NgConveyorState.WaitingForTransferRelease, system.Conveyor.Step);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));
            await Assert.ThrowsAsync<MotionInterlockException>(() => system.Conveyor.SetShuttleDownAsync(true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => system.Conveyor.ReturnFromConveyorAsync(stop.Token));

            // Unexpected Open feedback is not a commanded handoff.
            io.SetInputs((InputIo.NgCarrierGripperClosed, false), (InputIo.NgCarrierGripperOpen, true));
            Assert.True(transfer.IsTransferPending);
            Assert.Equal(NgConveyorState.WaitingForTransferRelease, system.Conveyor.Step);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));

            io.SetInputs((InputIo.NgCarrierPickupUp, false), (InputIo.NgCarrierPickupDown, true));
            await transfer.SetGripperOpenAsync(true, stop.Token);
            var placing = transfer.SetLiftUpAsync(true, stop.Token);
            Assert.False(transfer.IsTransferPending); // Only the completed release command clears it.
            Assert.False(placing.IsCompleted);
            Assert.Equal(NgConveyorState.WaitingForTransferRelease, system.Conveyor.Step);

            // Contradictory gripper feedback must still block even after release and ascent.
            io.SetInput(InputIo.NgCarrierGripperClosed, true);
            io.SetInputs((InputIo.NgCarrierPickupDown, false), (InputIo.NgCarrierPickupUp, true));
            await placing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(NgConveyorState.WaitingForTransferRelease, system.Conveyor.Step);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));
            io.SetInput(InputIo.NgCarrierGripperClosed, false);
            await lowering.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            stop.Cancel();
            if (repeat)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
            else
                await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task ShuttleReceiveChangeWakesWaitingInspectionTransfer()
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var io = system.Io;
        io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        io.SetInput(InputIo.NgCarrierEjectButton, true);
        Assert.False(system.Conveyor.IsReceiveAllowed);
        var lowering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgCarrierPickupDown && on)
                lowering.TrySetResult();
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = system.Inspection.RunAsync(stop.Token);
        try
        {
            Assert.False(run.IsCompleted);
            Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
            var assembly = system.Inspection.Station.GetAssembly(HeatSinkSlot.HeatSink1);
            assembly.RecordBoltPresence(VirtualTestSupport.BoltId(1), false);
            assembly.CompleteInspection();
            system.Inspection.Station.Complete(system.Inspection.Station.CurrentJob);
            // No transfer or carrier sensor changes: releasing the button alone must wake the loop.
            io.SetInput(InputIo.NgCarrierEjectButton, false);
            Assert.True(system.Conveyor.IsReceiveAllowed);
            await lowering.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task<(VirtualIoService Io, VirtualMotionService Motion, InspectionStation Inspection,
        NgCarrierConveyor Conveyor)> CreateAsync(UnitSettings? units = null)
    {
        var io = new VirtualIoService(Outputs(new NgCarrierTransferHardwareSettings(),
            new NgShuttleHardwareSettings(), new NgConveyorHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        units ??= new UnitSettings { MainConveyor = false };
        var settings = new NgCarrierTransferSettings
        {
            CarrierPickupPosition = new(),
            WaitingPosition = new(),
            ShuttlePlacePosition = new() { X = 10, Y = 10 },
        };
        var motionSettings = new InspectionGantrySettings();
        var operations = new OperationCancellation();
        var motion = new VirtualMotionService(motionSettings.Motion, operations, hasZ: false);
        motion.Initialize();
        await motion.HomeAsync(MotionAxis.X, 1_000);
        await motion.HomeAsync(MotionAxis.Y, 1_000);
        var work = ConveyorStation.CreateInspection(io);
        var conveyor = new NgCarrierConveyor(io, new(), units);
        var recipes = new RecipeManager(OpenMachineStore(), new());
        var inspection = new InspectionStation(
            work,
            motion,
            new MotionStatus(motion),
            conveyor,
            motionSettings,
            settings,
            io,
            units,
            new VirtualCamera(() => (0, 0, 0), () => []),
            new VirtualLightController(),
            new(),
            recipes);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await inspection.Station.SeatAsync(CancellationToken.None);
        return (io, motion, inspection, conveyor);
    }
}
