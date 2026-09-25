using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IBTM.Storage;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Theory]
    [InlineData(NgTransferDestination.Shuttle, InputIo.InspectionHeatSink1Present)]
    [InlineData(NgTransferDestination.Station, InputIo.NgShuttleUp)]
    public async Task NgPickupRechecksSourceAfterDescent(
        NgTransferDestination destination, InputIo lostInput)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var transfer = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await transfer.Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        io.SetInput(destination == NgTransferDestination.Shuttle
            ? InputIo.InspectionHeatSink1Present : InputIo.NgShuttleCarrierDetected, true);
        // Detection can remain ON even though the pickup has not gripped anything.
        io.SetInput(InputIo.NgCarrierDetected, true);
        var lost = false;
        var closed = false;
        var raisedAfterLoss = false;
        void LoseSourceDuringDescent(OutputIo output, bool on)
        {
            if (output == OutputIo.NgCarrierPickupDown && on && !lost)
            {
                lost = true;
                io.SetInput(lostInput, false);
            }
            closed |= lost && output == OutputIo.NgCarrierGripperClose && on;
            raisedAfterLoss |= lost && output == OutputIo.NgCarrierPickupDown && !on;
        }

        io.OutputChanged += LoseSourceDuringDescent;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            Assert.False(await transfer.ExecuteTransferAsync(
                destination, InspectionStationState.PickingCarrier, timeout.Token));
            Assert.True(lost);
            Assert.False(closed);
            Assert.False(raisedAfterLoss);
            Assert.False(transfer.IsTransferPending);
            Assert.Equal(NgTransferGripperState.Open, transfer.Gripper);

            io.OutputChanged -= LoseSourceDuringDescent;
            io.SetInput(lostInput, true);
            Assert.True(await transfer.ExecuteTransferAsync(
                destination, transfer.GetNextTransferStep(destination, canPickUp: true), timeout.Token));
            Assert.True(transfer.IsTransferPending);
            Assert.True(transfer.IsRaised);
            Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);
        }
        finally
        {
            io.OutputChanged -= LoseSourceDuringDescent;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NgTransferStopsBeforeLoweringAfterGripFeedbackLoss(bool opens)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.InspectionGantry.Motion.HorizontalSpeed = 200;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var move = services.GetRequiredService<InspectionStation>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var pickup = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await move.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, timeout.Token);
        var lost = false;
        var lowered = false;
        gantry.Motion.Feedback.PositionChanged += (x, y, z) =>
        {
            if (!lost && gantry.Motion.Feedback.IsMoving && x > 30)
            {
                lost = true;
                io.SetInputs((InputIo.NgCarrierGripperClosed, false), (InputIo.NgCarrierGripperOpen, opens));
            }
        };
        io.OutputChanged += (output, on) => lowered |= output == OutputIo.NgCarrierPickupDown && on;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => move.ExecuteTransferAsync(
                NgTransferDestination.Shuttle, InspectionStationState.PlacingCarrier, timeout.Token));
            Assert.True(lost);
            Assert.False(lowered);
            Assert.False(gantry.Motion.Feedback.IsMoving);
            Assert.True(pickup.IsTransferPending);
            var restarted = false;
            io.OutputChanged += (output, on) => restarted |= output is OutputIo.NgCarrierGripperClose
                or OutputIo.NgCarrierPickupDown;
            gantry.Motion.Feedback.MovingChanged += moving => restarted |= moving;
            await Assert.ThrowsAsync<InvalidOperationException>(() => move.ExecuteTransferAsync(
                NgTransferDestination.Shuttle,
                move.GetNextTransferStep(NgTransferDestination.Shuttle, canPickUp: true), timeout.Token));
            Assert.False(restarted);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SmemaTestInputsRequireTeachingAndClearWhenTheSelectorTurnsOff()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supply = services.GetRequiredService<PcbSupplier>();
        var conveyor = services.GetRequiredService<IBTM.Conveyor.MainConveyor>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        try
        {
            Assert.True(io.GetInput(InputIo.AutoMode)); // Teaching/manual contact ON.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
            io.SetInput(InputIo.MainConveyorReadyFromRear, true);
            Assert.False(supply.UpstreamCarrierAvailable);
            Assert.False(conveyor.UpstreamCarrierAvailable);
            Assert.False(conveyor.DownstreamReady);
            supply.TestUpstreamCarrierAvailable = true;
            conveyor.TestUpstreamCarrierAvailable = true;
            conveyor.TestDownstreamReady = true;
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            Assert.True(supply.UpstreamCarrierAvailable);
            Assert.True(conveyor.UpstreamCarrierAvailable);
            Assert.True(conveyor.DownstreamReady);

            io.SetInput(InputIo.AutoMode, false);
            Assert.False(supply.TestUpstreamCarrierAvailable);
            Assert.False(conveyor.TestUpstreamCarrierAvailable);
            Assert.False(conveyor.TestDownstreamReady);
            Assert.False(supply.UpstreamCarrierAvailable);
            Assert.False(conveyor.UpstreamCarrierAvailable);
            Assert.False(conveyor.DownstreamReady);

            // Direct calls cannot enable TEST while the teaching switch is OFF.
            supply.TestUpstreamCarrierAvailable = true;
            conveyor.TestUpstreamCarrierAvailable = true;
            conveyor.TestDownstreamReady = true;
            Assert.False(supply.TestUpstreamCarrierAvailable);
            Assert.False(conveyor.TestUpstreamCarrierAvailable);
            Assert.False(conveyor.TestDownstreamReady);

            // Real SMEMA remains usable in AUTO.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
            io.SetInput(InputIo.MainConveyorReadyFromRear, true);
            Assert.True(supply.UpstreamCarrierAvailable);
            Assert.True(conveyor.UpstreamCarrierAvailable);
            Assert.True(conveyor.DownstreamReady);
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            io.SetInput(InputIo.AutoMode, true);
            Assert.False(supply.UpstreamCarrierAvailable);
            Assert.False(conveyor.UpstreamCarrierAvailable);
            Assert.False(conveyor.DownstreamReady);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task FasteningCompletionCannotCompleteAReplacementCarrier()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        services.GetRequiredService<RecipeManager>().Current.Pcb.BoltPoints =
            [new() { Number = 1, Head = FasteningHead.Shooting, X = 0, Y = 0, FasteningX = 0, FasteningY = 0 }];
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var previousAssembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var replaced = false;
        void ReplaceAfterResult(HeatSinkAssembly assembly)
        {
            if (replaced || !assembly.PcbBoltResults.ContainsKey(1))
                return;
            replaced = true;
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            stop.Cancel();
        }

        previousAssembly.ResultsChanged += ReplaceAfterResult;
        try
        {
            await station.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(replaced);
            Assert.True(previousAssembly.PcbBoltResults[1].Success);
            Assert.Empty(work.Assemblies);
            Assert.False(work.Completed);
            Assert.False(gantry.Motion.Feedback.IsMoving);
        }
        finally
        {
            previousAssembly.ResultsChanged -= ReplaceAfterResult;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplyDoesNotAdvanceTheNewCarrierWhenAnOldPickupFinishes(bool testSignal)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supply = services.GetRequiredService<PcbSupplier>();
        var handler = services.GetRequiredService<PcbSupplier>();
        var motion = handler.Motion.Feedback;
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 10, Z = 5 },
        };
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.PcbSupplyRotated, true);
        io.SetInput(InputIo.PcbSupplyUnrotated, false);
        io.SetInput(InputIo.PcbSupplyPcbDetected, false);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
        io.SetInput(InputIo.AutoMode, testSignal);
        void SetCarrierAvailable(bool available)
        {
            if (testSignal)
                handler.TestUpstreamCarrierAvailable = available;
            else
                io.SetInput(InputIo.PcbSupplyAvailableFromFront1, available);
        }
        SetCarrierAvailable(true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var firstSlotVisits = 0;
        var atFirstSlot = false;
        var skippedFirstSlot = false;
        void ChangeCarrierAtPickup(double x, double y, double z)
        {
            var atPickup = Math.Abs(x - 10) < 0.01
                && Math.Abs(y - recipe.Pcb1PickPosition.Y!.Value) < 0.01
                && Math.Abs(z - 5) < 0.01;
            if (atPickup && !atFirstSlot)
            {
                firstSlotVisits++;
                if (firstSlotVisits == 1)
                {
                    SetCarrierAvailable(false);
                    SetCarrierAvailable(true);
                }
                else
                    stop.Cancel();
            }
            atFirstSlot = atPickup;
            if (firstSlotVisits == 1 && x > 15)
            {
                skippedFirstSlot = true;
                stop.Cancel();
            }
        }

        motion.PositionChanged += ChangeCarrierAtPickup;
        try
        {
            await supply.RunAsync(recipe, services.GetRequiredService<PcbPlacer>(), stop.Token).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(skippedFirstSlot);
            Assert.Equal(2, firstSlotVisits);
            Assert.False(motion.IsMoving);
            Assert.Equal(!testSignal, io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
        }
        finally
        {
            motion.PositionChanged -= ChangeCarrierAtPickup;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatNgTransferReturnsToPickupThenSeparateWaitingPosition(bool hasWaitingPosition)
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var move = services.GetRequiredService<InspectionStation>();
        var settings = services.GetRequiredService<NgCarrierTransferSettings>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var pickup = services.GetRequiredService<InspectionStation>();
        var io = services.GetRequiredService<VirtualIoService>();
        settings.WaitingPosition = hasWaitingPosition ? new() { X = 15, Y = 30 } : null;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        // Repeat holds the carrier above the shuttle and returns without releasing it.
        io.SetInputs(
            (InputIo.InspectionHeatSink1Present, true),
            (InputIo.InspectionHeatSink2Present, true));
        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        await gantry.MoveToAsync(settings.CarrierPickupPosition!, 10_000);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        using var transferTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await move.ExecuteTransferAsync(NgTransferDestination.Shuttle,
            InspectionStationState.PickingCarrier, transferTimeout.Token, holdAtDestination: true);
        await move.ExecuteTransferAsync(NgTransferDestination.Shuttle,
            InspectionStationState.PlacingCarrier, transferTimeout.Token, holdAtDestination: true);
        Assert.True(move.IsTransferPending);
        Assert.Equal(NgTransferGripperState.Closed, pickup.Gripper);
        Assert.True(pickup.IsRaised);
        Assert.True(gantry.Motion.IsAt(settings.ShuttlePlacePosition));
        Assert.False(io.GetInput(InputIo.NgShuttleCarrierDetected));
        await move.Station.PrepareToReceiveAsync(transferTimeout.Token);
        var raisedAtPickup = false;
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.InspectionBackupPlateUp || !on)
                return;
            Assert.True(gantry.Motion.IsAt(settings.CarrierPickupPosition!));
            Assert.True(pickup.IsRaised);
            raisedAtPickup = true;
        };
        feedback.AxisMoves.Clear();
        var movedBeforeGrip = false;
        gantry.Motion.Feedback.PositionChanged += (_, _, _) =>
        {
            if (pickup.Gripper != NgTransferGripperState.Closed)
                movedBeforeGrip = true;
        };

        await move.ReturnToStationAsync(transferTimeout.Token).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(move.IsTransferPending);
        Assert.True(raisedAtPickup);
        Assert.False(movedBeforeGrip);
        Assert.Empty(feedback.AxisMoves);
        Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.False(io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.True(pickup.IsRaised);
        Assert.Equal(NgTransferGripperState.Open, pickup.Gripper);
        Assert.True(gantry.Motion.IsAt(settings.CarrierPickupPosition!));
        if (!hasWaitingPosition)
        {
            var position = settings.CarrierPickupPosition!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => move.ClearStationAsync(CancellationToken.None));
            Assert.Empty(feedback.AxisMoves);
            Assert.True(gantry.Motion.IsAt(position));
            return;
        }

        var movedAfterReturn = false;
        gantry.Motion.Feedback.PositionChanged += (x, y, z) => movedAfterReturn = true;
        await move.ClearStationAsync(CancellationToken.None);
        Assert.True(movedAfterReturn);
        Assert.Empty(feedback.AxisMoves);
        Assert.True(gantry.Motion.IsAt(settings.WaitingPosition!));
        Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.False(io.GetInput(InputIo.NgCarrierDetected));
    }

    [Fact]
    public async Task NgPickupMovesXyTogetherAndResumesBeforeGripping()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var move = services.GetRequiredService<InspectionStation>();
        var settings = services.GetRequiredService<NgCarrierTransferSettings>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 50, Y = 60 }, 10_000);

        settings.CarrierPickupPosition = null;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => move.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None)!);
        Assert.Empty(feedback.AxisMoves);
        Assert.Equal((50, 60, 0), gantry.Motion.Feedback.Position);

        settings.CarrierPickupPosition = new() { X = 5, Y = 20 };
        services.GetRequiredService<InspectionGantrySettings>().Motion.HorizontalSpeed = 100;
        settings.ShuttlePlacePosition = new() { X = 150, Y = 80 };
        var pickupPosition = settings.CarrierPickupPosition!;
        using var cancellation = new CancellationTokenSource();
        void StopDuringPickupMove(double x, double y, double z)
        {
            if (x > pickupPosition.X && x < 49
                && y > pickupPosition.Y && y < 59)
                cancellation.Cancel();
        }
        gantry.Motion.Feedback.PositionChanged += StopDuringPickupMove;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => move.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, cancellation.Token)!);
        gantry.Motion.Feedback.PositionChanged -= StopDuringPickupMove;
        Assert.Empty(feedback.AxisMoves);
        var stopped = gantry.Motion.Feedback.Position;
        Assert.InRange(stopped.X, pickupPosition.X + 0.01, 49.99);
        Assert.InRange(stopped.Y, pickupPosition.Y + 0.01, 59.99);
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.False(io.GetInput(InputIo.NgCarrierDetected));

        feedback.AxisMoves.Clear();
        Assert.False(await move.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None));
        Assert.Empty(feedback.AxisMoves);
        Assert.True(gantry.Motion.IsAt(pickupPosition));
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.Equal(settings.CarrierPickupPosition!.X, gantry.Motion.Feedback.Position.X);

        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        Assert.Equal(InspectionStationState.PickingCarrier, move.GetNextTransferStep(NgTransferDestination.Shuttle, canPickUp: true));
        await move.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None)!;
        Assert.Equal(InspectionStationState.PlacingCarrier, move.GetNextTransferStep(NgTransferDestination.Shuttle, canPickUp: true));
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        Assert.Equal(5, gantry.Motion.Feedback.Position.X);
        Assert.Empty(feedback.AxisMoves);

        feedback.AxisMoves.Clear();
        var axesMovedTogether = false;
        void ObserveShuttleMove(double x, double y, double z)
        {
            axesMovedTogether |= x > pickupPosition.X && x < settings.ShuttlePlacePosition.X
                && y > pickupPosition.Y && y < settings.ShuttlePlacePosition.Y;
        }
        gantry.Motion.Feedback.PositionChanged += ObserveShuttleMove;
        try
        {
            await move.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PlacingCarrier, CancellationToken.None)!;
        }
        finally
        {
            gantry.Motion.Feedback.PositionChanged -= ObserveShuttleMove;
        }
        Assert.True(axesMovedTogether);
        Assert.Empty(feedback.AxisMoves);
        Assert.True(gantry.Motion.IsAt(settings.ShuttlePlacePosition));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementDoesNotPressAfterCarrierChangesDuringIpmRetraction(bool replaceCarrier)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.PcbSupply = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var handler = services.GetRequiredService<PcbPlacer>();
        var placer = services.GetRequiredService<PcbPlacer>();
        var work = services.GetRequiredService<PcbPlacer>().Station;
        var recipe = services.GetRequiredService<RecipeManager>().Current.PcbPlacement;
        recipe.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 10 };
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await handler.MoveToXYAsync(recipe.HeatSink1PcbPlacementPosition);
        await handler.MoveAxisAsync(MotionAxis.Z, 10);
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.PcbPlacementIpmDown, true);
        foreach (var input in new[]
        {
            InputIo.PcbPlacementHeatSink1Present,
            InputIo.PcbPlacementBackupPlateUp,
            InputIo.PcbPlacementStopperDown,
            InputIo.PcbPlacementHeatSink1Present,
            InputIo.PcbPlacementPcbDetected,
            InputIo.PcbPlacementHandlerUnrotated,
            InputIo.PcbPlacementHandlerDown,
            InputIo.PcbPlacementIpmDown,
        })
            io.SetInput(input, true);
        foreach (var input in new[]
        {
            InputIo.PcbPlacementBackupPlateDown,
            InputIo.PcbPlacementStopperUp,
            InputIo.PcbPlacementHandlerRotated,
            InputIo.PcbPlacementHandlerUp,
            InputIo.PcbPlacementIpmUp,
        })
            io.SetInput(input, false);
        var pressed = false;
        void ChangeCarrierOnRetraction(OutputIo output, bool on)
        {
            pressed |= output == OutputIo.PcbPlacementIpmDown && on;
            if (output != OutputIo.PcbPlacementIpmDown || on)
                return;
            io.SetInputs((InputIo.PcbPlacementIpmDown, false), (InputIo.PcbPlacementIpmUp, true));
            if (replaceCarrier)
            {
                VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
                VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            }
            else
                io.SetInput(InputIo.PcbPlacementBackupPlateUp, false);
        }

        io.OutputChanged += ChangeCarrierOnRetraction;
        try
        {
            Assert.Equal(PcbPlacementState.PlacingPcb, placer.Phase);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None)!);
            Assert.Contains("carrier", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(pressed);
            Assert.Empty(work.Assemblies);
        }
        finally
        {
            io.OutputChanged -= ChangeCarrierOnRetraction;
            await machine.ShutdownAsync();
        }
    }

    [Trait("Category", "MachineFlow")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementPressTracksOnlyTheSameCarrierAndRequiresDownFeedback(bool replaceCarrier)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.PcbSupply = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var handler = services.GetRequiredService<PcbPlacer>();
        var placer = services.GetRequiredService<PcbPlacer>();
        var work = services.GetRequiredService<PcbPlacer>().Station;
        var recipe = services.GetRequiredService<RecipeManager>().Current.PcbPlacement;
        recipe.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 10 };
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await handler.MoveToXYAsync(recipe.HeatSink1PcbPlacementPosition);
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.PcbPlacementIpmDown, true);
        foreach (var input in new[]
        {
            InputIo.PcbPlacementHeatSink1Present,
            InputIo.PcbPlacementBackupPlateUp,
            InputIo.PcbPlacementStopperDown,
            InputIo.PcbPlacementHeatSink1Present,
            InputIo.PcbPlacementPcbDetected,
            InputIo.PcbPlacementHandlerUnrotated,
            InputIo.PcbPlacementIpmDown
        })
            io.SetInput(input, true);
        foreach (var input in new[]
        {
            InputIo.PcbPlacementBackupPlateDown,
            InputIo.PcbPlacementStopperUp,
            InputIo.PcbPlacementHandlerRotated,
            InputIo.PcbPlacementIpmUp
        })
            io.SetInput(input, false);
        // XY alone is not a placement position, even with PCB detection and vacuum OFF.
        Assert.DoesNotContain(
            placer.Phase,
            new[] { PcbPlacementState.PlacingPcb });
        await handler.MoveAxisAsync(MotionAxis.Z, 10);
        Assert.DoesNotContain(
            placer.Phase,
            new[] { PcbPlacementState.PlacingPcb });
        io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        io.SetInput(InputIo.PcbPlacementHandlerDown, true);
        io.SetOutput(OutputIo.PcbPlacementHandlerDown, true);

        var outputs = new List<(OutputIo, bool)>();
        using var preparing = new CancellationTokenSource();
        using var pressing = new CancellationTokenSource();
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementHandlerDown)
            {
                io.SetInputs((InputIo.PcbPlacementHandlerDown, on), (InputIo.PcbPlacementHandlerUp, !on));
                return;
            }
            if (output != OutputIo.PcbPlacementIpmDown)
                return;
            outputs.Add((output, on));
            var feedback = io.GetOutputFeedback(output)!;
            io.SetInput(on ? feedback.OffInput!.Value : feedback.OnInput, false);
            if (output == OutputIo.PcbPlacementIpmDown && on)
            {
                pressing.Cancel(); // Stop between Up and Down feedback.
                return;
            }

            io.SetInput(on ? feedback.OnInput : feedback.OffInput!.Value, true);
            if (!on)
                preparing.Cancel();
        };

        Assert.Equal(PcbPlacementState.PlacingPcb, placer.Phase);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, preparing.Token)!);
        Assert.Equal(PlacementCylinderState.Up, handler.IpmLift);
        Assert.Equal(PcbPlacementState.PlacingPcb, placer.Phase);
        Assert.Empty(work.Assemblies);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, pressing.Token)!);
        Assert.Equal(PlacementCylinderState.Between, handler.IpmLift);
        Assert.Equal(PcbPlacementState.PlacingPcb, placer.Phase);
        Assert.Empty(work.Assemblies);
        io.SetInput(InputIo.PcbPlacementIpmDown, true);
        Assert.Equal(PcbPlacementState.PlacingPcb, placer.Phase);
        if (replaceCarrier)
        {
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            Assert.Equal(PcbPlacementState.PlacingPcb, placer.Phase);
            Assert.Empty(work.Assemblies);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, pressing.Token)!);
            Assert.Empty(work.Assemblies);
            await placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None)!;
            Assert.Single(work.Assemblies);
        }
        var expected = new List<(OutputIo, bool)>
        {
            (OutputIo.PcbPlacementIpmDown, false),
            (OutputIo.PcbPlacementIpmDown, true),
        };
        if (!replaceCarrier)
        {
            expected.Add((OutputIo.PcbPlacementIpmDown, false));
            Assert.Equal(PlacementCylinderState.Up, handler.Lift);
            Assert.True(handler.IsAtHorizontalZ);
        }
        Assert.Equal(expected, outputs);
        await machine.ShutdownAsync();
    }

    [Theory]
    [InlineData(NgTransferDestination.Station)]
    [InlineData(NgTransferDestination.Shuttle)]
    public async Task NgTransferUsesTheSameLiveReleaseStatesInBothDirections(
        NgTransferDestination destination)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var move = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        // Establish pickup ownership through the real sequence, independently of presence DI.
        await move.ExecuteTransferAsync(destination, InspectionStationState.PickingCarrier,
            CancellationToken.None, allowEmpty: true);
        await services.GetRequiredService<InspectionStation>()
            .MoveToAsync(
                destination == NgTransferDestination.Station
                    ? settings.NgCarrierTransfer.CarrierPickupPosition!
                    : settings.NgCarrierTransfer.ShuttlePlacePosition,
                1_000);
        io.AutoResponseEnabled = false;
        var destinationSensor = destination == NgTransferDestination.Station
            ? InputIo.InspectionHeatSink1Present
            : InputIo.NgShuttleCarrierDetected;
        io.SetInput(InputIo.NgCarrierDetected, true);
        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, true);
        Assert.Equal(
            InspectionStationState.WaitingForDestination,
            move.GetNextTransferStep(destination, canPickUp: true, canReceive: false));
        io.SetInput(destinationSensor, true);
        AssertState(InspectionStationState.WaitingForDestination);
        // The descending held carrier can enter the support sensor before Down.
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        AssertState(InspectionStationState.PlacingCarrier);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        AssertState(InspectionStationState.PlacingCarrier);
        if (destination == NgTransferDestination.Shuttle)
        {
            io.SetInput(InputIo.NgShuttleUp, false);
            io.SetInput(InputIo.NgShuttleDown, false);
            Assert.Equal(InspectionStationState.PreparingTransfer,
                move.GetNextTransferStep(destination, canPickUp: true, holdAtDestination: true));
            AssertState(InspectionStationState.WaitingForDestination);
            io.SetInput(InputIo.NgCarrierDetected, false);
            Assert.Equal(InspectionStationState.PreparingTransfer,
                move.GetNextTransferStep(destination, canPickUp: true, holdAtDestination: true));
            io.SetInput(InputIo.NgCarrierDetected, true);
            io.SetInput(InputIo.NgCarrierPickupDown, false);
            io.SetInput(InputIo.NgCarrierPickupUp, true);
            Assert.Equal(InspectionStationState.HoldingAtDestination,
                move.GetNextTransferStep(destination, canPickUp: true, holdAtDestination: true));
            io.SetInput(InputIo.NgCarrierPickupUp, false);
            io.SetInput(InputIo.NgCarrierGripperClosed, false);
            io.SetInput(InputIo.NgCarrierGripperOpen, true);
            // Unexpected opening above an unsupported destination must retain the failed transfer.
            Assert.Equal(InspectionStationState.PickingCarrier,
                move.GetNextTransferStep(destination, canPickUp: true, holdAtDestination: true));
            io.SetInput(InputIo.NgCarrierPickupDown, true);
            io.SetInput(InputIo.NgCarrierGripperClosed, true);
            io.SetInput(InputIo.NgCarrierGripperOpen, false);
            io.SetInput(InputIo.NgShuttleUp, true);
            var gripperOutput = io.GetOutput(OutputIo.NgCarrierGripperClose);
            Assert.False(await move.ExecuteTransferAsync(
                destination, InspectionStationState.HoldingAtDestination, CancellationToken.None));
            Assert.Equal(gripperOutput, io.GetOutput(OutputIo.NgCarrierGripperClose));
        }
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        AssertState(InspectionStationState.PlacingCarrier);
        io.SetInput(InputIo.NgCarrierGripperOpen, true);
        var pickup = services.GetRequiredService<InspectionStation>();
        Assert.True(pickup.IsTransferPending);
        using (var stop = new CancellationTokenSource())
        {
            var placing = move.ExecuteTransferAsync(destination, InspectionStationState.PlacingCarrier, stop.Token);
            Assert.False(pickup.IsTransferPending);
            Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => placing);
        }
        io.SetInput(destinationSensor, false);
        AssertState(InspectionStationState.PlacingCarrier);
        io.SetInput(destinationSensor, true);
        AssertState(InspectionStationState.PlacingCarrier);
        io.SetInput(InputIo.NgCarrierPickupDown, false);
        io.SetInput(InputIo.NgCarrierPickupUp, true);
        AssertState(InspectionStationState.TransferCompleted);
        await machine.ShutdownAsync();

        void AssertState(InspectionStationState expected)
        {
            Assert.Equal(expected, move.GetNextTransferStep(destination, canPickUp: false));
            Assert.Equal(expected, move.GetNextTransferStep(destination, canPickUp: true));
        }
    }
}
