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
    [Fact]
    public async Task SmemaTestInputsRequireTeachingAndClearWhenTheSelectorTurnsOff()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supply = services.GetRequiredService<PcbSupplyHandler>();
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
        settings.Units.ShootingBoltFeeder = true;
        await using var services = CreateServices(settings);
        services.GetRequiredService<RecipeManager>().Current.Pcb.BoltPoints =
            [new() { Number = 1, Head = FasteningHead.Shooting, X = 0, Y = 0 }];
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningWork>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var operations = services.GetRequiredService<OperationCancellation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        var previousAssembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        previousAssembly.RecordPcbBolt(1, new(true, 1));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var replaced = false;
        void ReplaceAfterFinalMove()
        {
            if (replaced || operations.HasActiveOperations)
                return;
            replaced = true;
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            stop.Cancel();
        }

        operations.ActivityChanged += ReplaceAfterFinalMove;
        try
        {
            Assert.Equal(BoltFasteningState.CompletingCarrier, station.GetState());
            await station.RunAsync(new(), stop.Token).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(replaced);
            Assert.NotEqual(AssemblyResult.Pending, previousAssembly.FasteningResult);
            Assert.Empty(work.Assemblies);
            Assert.False(work.Completed);
            Assert.False(gantry.Feedback.IsMoving);
        }
        finally
        {
            operations.ActivityChanged -= ReplaceAfterFinalMove;
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
        var handler = services.GetRequiredService<PcbSupplyHandler>();
        var motion = handler.Feedback;
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Z = 5 },
        };
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.PcbSupplyRotated, false);
        io.SetInput(InputIo.PcbSupplyUnrotated, true);
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
                && Math.Abs(y - settings.PcbSupply.CarrierY) < 0.01
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
            await supply.RunAsync(recipe, stop.Token).WaitAsync(TimeSpan.FromSeconds(4));
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
    public async Task ReverseNgPickupStaysAtShuttleAndClearsStationOnlyWithFirstFov(bool hasFov)
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var move = services.GetRequiredService<NgCarrierMove>();
        var settings = services.GetRequiredService<NgCarrierTransferSettings>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var pickup = services.GetRequiredService<NgCarrierTransfer>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(settings.ShuttlePlacePosition, 10_000);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        var gripped = false;
        var movedBeforeGrip = false;
        io.InputChanged += (input, value) =>
        {
            if (input == InputIo.NgCarrierDetected && value)
                gripped = true;
        };
        gantry.Feedback.PositionChanged += (_, _, _) =>
        {
            if (!gripped)
                movedBeforeGrip = true;
        };

        await move.ReturnToStationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(gripped);
        Assert.False(movedBeforeGrip);
        Assert.Empty(feedback.AxisMoves);
        Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.False(io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.True(pickup.IsRaised);
        Assert.Equal(NgTransferGripperState.Open, pickup.Gripper);
        Assert.True(gantry.IsAt(settings.GetCarrierPickupPosition()!));
        if (!hasFov)
        {
            var position = settings.GetCarrierPickupPosition()!;
            settings.PickupSafeX = null;
            await move.ClearStationAsync(null, CancellationToken.None);
            Assert.Empty(feedback.AxisMoves);
            Assert.True(gantry.IsAt(position));
            return;
        }

        var firstFov = new AxisPosition { X = 30, Y = 40 };
        using var cancellation = new CancellationTokenSource();
        void StopAtFovY(double x, double y, double z)
        {
            if (y == firstFov.Y)
                cancellation.Cancel();
        }
        gantry.Feedback.PositionChanged += StopAtFovY;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => move.ClearStationAsync(firstFov, cancellation.Token));
        gantry.Feedback.PositionChanged -= StopAtFovY;
        Assert.Equal(new[] { (MotionAxis.X, 5d), (MotionAxis.Y, 40d) }, feedback.AxisMoves);
        Assert.Equal(5, gantry.Feedback.GetPosition().X);

        feedback.AxisMoves.Clear();
        await move.ClearStationAsync(firstFov, CancellationToken.None);
        Assert.Equal(
            new[] { (MotionAxis.X, 5d), (MotionAxis.Y, 40d), (MotionAxis.X, 30d) },
            feedback.AxisMoves);
        Assert.True(gantry.IsAt(firstFov));
        Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.False(pickup.CarrierDetected);
    }

    [Fact]
    public async Task NgPickupApproachUsesSafeXThenYAndGripsWithoutAnotherXMove()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var move = services.GetRequiredService<NgCarrierMove>();
        var settings = services.GetRequiredService<NgCarrierTransferSettings>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 50, Y = 60 }, 10_000);

        settings.PickupSafeX = null;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.MovingToCarrier, CancellationToken.None)!);
        Assert.Empty(feedback.AxisMoves);
        Assert.Equal((50, 60, 0), gantry.Feedback.GetPosition());

        settings.PickupSafeX = 5;
        using var cancellation = new CancellationTokenSource();
        void StopAtSafeX(double x, double y, double z)
        {
            if (x == settings.PickupSafeX)
                cancellation.Cancel();
        }
        gantry.Feedback.PositionChanged += StopAtSafeX;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.MovingToCarrier, cancellation.Token)!);
        gantry.Feedback.PositionChanged -= StopAtSafeX;
        Assert.Equal(new[] { (MotionAxis.X, 5d) }, feedback.AxisMoves);
        Assert.Equal(60, gantry.Feedback.GetPosition().Y);

        feedback.AxisMoves.Clear();
        await move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.MovingToCarrier, CancellationToken.None)!;
        Assert.Equal(
            new[] { (MotionAxis.X, 5d), (MotionAxis.Y, settings.CarrierPickupPosition.Y) },
            feedback.AxisMoves);
        Assert.NotEqual(settings.CarrierPickupPosition.X, gantry.Feedback.GetPosition().X);

        await services.GetRequiredService<InspectionWork>().Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        Assert.Equal(NgTransferState.LoweringToCarrier, move.GetState(NgTransferDestination.Shuttle, canPickUp: true));
        await move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.LoweringToCarrier, CancellationToken.None)!;
        Assert.Equal(NgTransferState.Closing, move.GetState(NgTransferDestination.Shuttle, canPickUp: true));
        await move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.Closing, CancellationToken.None)!;
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        Assert.Equal(5, gantry.Feedback.GetPosition().X);
        Assert.Equal(2, feedback.AxisMoves.Count);
        await move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.Raising, CancellationToken.None)!;

        feedback.AxisMoves.Clear();
        await move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.MovingToDestination, CancellationToken.None)!;
        Assert.Empty(feedback.AxisMoves);
        Assert.True(gantry.IsAt(settings.ShuttlePlacePosition));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementDoesNotPressAfterCarrierChangesDuringGripperClose(bool replaceCarrier)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.PcbSupply = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var handler = services.GetRequiredService<PcbPlacementHandler>();
        var placer = services.GetRequiredService<PcbPlacer>();
        var work = services.GetRequiredService<PcbPlacementWork>();
        var recipe = services.GetRequiredService<RecipeManager>().Current.PcbPlacement;
        recipe.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 10 };
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await handler.MoveToXYAsync(recipe.HeatSink1PcbPlacementPosition);
        await handler.MoveAxisAsync(MotionAxis.Z, 10);
        io.AutoResponseEnabled = false;
        foreach (var input in new[]
        {
            InputIo.PcbPlacementHeatSink1Present,
            InputIo.PcbPlacementBackupPlateUp,
            InputIo.PcbPlacementStopperDown,
            InputIo.PcbPlacementHeatSink1Present,
            InputIo.PcbPlacementPcbDetected,
            InputIo.PcbPlacementIpmGripperOpen,
            InputIo.PcbPlacementHandlerRotated,
            InputIo.PcbPlacementHandlerDown,
            InputIo.PcbPlacementIpmUp,
        })
            io.SetInput(input, true);
        foreach (var input in new[]
        {
            InputIo.PcbPlacementBackupPlateDown,
            InputIo.PcbPlacementStopperUp,
            InputIo.PcbPlacementIpmGripperClosed,
            InputIo.PcbPlacementHandlerUnrotated,
            InputIo.PcbPlacementHandlerUp,
            InputIo.PcbPlacementIpmDown,
        })
            io.SetInput(input, false);
        var pressed = false;
        void ChangeCarrierOnClose(OutputIo output, bool on)
        {
            pressed |= output == OutputIo.PcbPlacementIpmDown && on;
            if (output != OutputIo.PcbPlacementIpmGripperClose || !on)
                return;
            io.SetInput(InputIo.PcbPlacementIpmGripperOpen, false);
            io.SetInput(InputIo.PcbPlacementIpmGripperClosed, true);
            if (replaceCarrier)
            {
                VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
                VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            }
            else
                io.SetInput(InputIo.PcbPlacementBackupPlateUp, false);
        }

        io.OutputChanged += ChangeCarrierOnClose;
        try
        {
            Assert.Equal(PcbPlacementState.PressingPcb, placer.GetState(recipe));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, CancellationToken.None)!);
            Assert.Contains("carrier", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(pressed);
            Assert.Empty(work.Assemblies);
        }
        finally
        {
            io.OutputChanged -= ChangeCarrierOnClose;
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
        var handler = services.GetRequiredService<PcbPlacementHandler>();
        var placer = services.GetRequiredService<PcbPlacer>();
        var work = services.GetRequiredService<PcbPlacementWork>();
        var recipe = services.GetRequiredService<RecipeManager>().Current.PcbPlacement;
        recipe.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 10 };
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await handler.MoveToXYAsync(recipe.HeatSink1PcbPlacementPosition);
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.PcbPlacementIpmGripperClose, true);
        io.SetOutput(OutputIo.PcbPlacementIpmDown, true);
        foreach (var input in new[]
        {
            InputIo.PcbPlacementHeatSink1Present,
            InputIo.PcbPlacementBackupPlateUp,
            InputIo.PcbPlacementStopperDown,
            InputIo.PcbPlacementHeatSink1Present,
            InputIo.PcbPlacementPcbDetected,
            InputIo.PcbPlacementIpmGripperClosed,
            InputIo.PcbPlacementHandlerRotated,
            InputIo.PcbPlacementIpmDown
        })
            io.SetInput(input, true);
        foreach (var input in new[]
        {
            InputIo.PcbPlacementBackupPlateDown,
            InputIo.PcbPlacementStopperUp,
            InputIo.PcbPlacementIpmGripperOpen,
            InputIo.PcbPlacementHandlerUnrotated,
            InputIo.PcbPlacementIpmUp
        })
            io.SetInput(input, false);
        // XY alone is not a placement position, even with PCB detection and vacuum OFF.
        Assert.DoesNotContain(
            placer.GetState(recipe),
            new[] { PcbPlacementState.PressingPcb, PcbPlacementState.RecordingPlacement });
        await handler.MoveAxisAsync(MotionAxis.Z, 10);
        Assert.DoesNotContain(
            placer.GetState(recipe),
            new[] { PcbPlacementState.PressingPcb, PcbPlacementState.RecordingPlacement });
        io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        io.SetInput(InputIo.PcbPlacementHandlerDown, true);

        var outputs = new List<(OutputIo, bool)>();
        using var closing = new CancellationTokenSource();
        using var pressing = new CancellationTokenSource();
        io.OutputChanged += (output, on) =>
        {
            if (output is not (OutputIo.PcbPlacementIpmDown or OutputIo.PcbPlacementIpmGripperClose))
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
            if (output == OutputIo.PcbPlacementIpmGripperClose && on)
                closing.Cancel();
        };

        Assert.Equal(PcbPlacementState.OpeningGripper, placer.GetState(recipe));
        await placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, CancellationToken.None)!;
        Assert.Equal(PcbPlacementState.RaisingIpm, placer.GetState(recipe));
        await placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, CancellationToken.None)!;
        Assert.Equal(PcbPlacementState.PressingPcb, placer.GetState(recipe));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, closing.Token)!);
        Assert.Equal(PlacementGripperState.Closed, handler.IpmGripper);
        Assert.Equal(PcbPlacementState.PressingPcb, placer.GetState(recipe));
        Assert.Empty(work.Assemblies);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, pressing.Token)!);
        Assert.Equal(PlacementCylinderState.Between, handler.IpmLift);
        Assert.Equal(PcbPlacementState.PressingPcb, placer.GetState(recipe));
        Assert.Empty(work.Assemblies);
        io.SetInput(InputIo.PcbPlacementIpmDown, true);
        Assert.Equal(PcbPlacementState.RecordingPlacement, placer.GetState(recipe));
        if (replaceCarrier)
        {
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            Assert.Equal(PcbPlacementState.OpeningGripper, placer.GetState(recipe));
            Assert.Empty(work.Assemblies);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, pressing.Token)!);
            Assert.Empty(work.Assemblies);
            await placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, CancellationToken.None)!;
            Assert.Single(work.Assemblies);
        }
        Assert.Equal(
            new[] { (OutputIo.PcbPlacementIpmGripperClose, false), (
                OutputIo.PcbPlacementIpmDown,
                false), (
                    OutputIo.PcbPlacementIpmGripperClose,
                    true), (
                        OutputIo.PcbPlacementIpmDown,
                        true) },
            outputs);
        await machine.ShutdownAsync();
    }

    [Theory]
    [InlineData(NgTransferDestination.Station)]
    [InlineData(NgTransferDestination.Shuttle)]
    public async Task NgTransferUsesTheSameLiveReleaseStatesInBothDirections(
        NgTransferDestination destination)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var move = services.GetRequiredService<NgCarrierMove>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await services.GetRequiredService<InspectionWork>().Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        await services.GetRequiredService<InspectionGantry>()
            .MoveToAsync(
                destination == NgTransferDestination.Station
                    ? settings.NgCarrierTransfer.GetCarrierPickupPosition()!
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
            NgTransferState.WaitingForDestination,
            move.GetState(destination, canPickUp: true, canReceive: false));
        io.SetInput(destinationSensor, true);
        AssertState(NgTransferState.WaitingForDestination);
        // The descending held carrier can enter the support sensor before Down.
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        AssertState(NgTransferState.LoweringAtDestination);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        AssertState(NgTransferState.Opening);
        if (destination == NgTransferDestination.Shuttle)
        {
            io.SetInput(InputIo.NgShuttleUp, false);
            io.SetInput(InputIo.NgShuttleDown, false);
            Assert.Equal(NgTransferState.HoldingAtDestination,
                move.GetState(destination, canPickUp: true, holdAtDestination: true));
            AssertState(NgTransferState.ShuttleNotReady);
            io.SetInput(InputIo.NgCarrierDetected, false);
            Assert.Equal(NgTransferState.WaitingForGrip,
                move.GetState(destination, canPickUp: true, holdAtDestination: true));
            io.SetInput(InputIo.NgCarrierDetected, true);
            io.SetInput(InputIo.NgCarrierPickupDown, false);
            io.SetInput(InputIo.NgCarrierGripperClosed, false);
            io.SetInput(InputIo.NgCarrierGripperOpen, true);
            Assert.Equal(NgTransferState.Closing,
                move.GetState(destination, canPickUp: true, holdAtDestination: true));
            io.SetInput(InputIo.NgCarrierPickupDown, true);
            io.SetInput(InputIo.NgCarrierGripperClosed, true);
            io.SetInput(InputIo.NgCarrierGripperOpen, false);
            io.SetInput(InputIo.NgShuttleUp, true);
            var gripperOutput = io.GetOutput(OutputIo.NgCarrierGripperClose);
            Assert.Null(move.ExecuteAsync(
                destination, NgTransferState.HoldingAtDestination, CancellationToken.None));
            Assert.Equal(gripperOutput, io.GetOutput(OutputIo.NgCarrierGripperClose));
        }
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        AssertState(NgTransferState.Opening);
        io.SetInput(InputIo.NgCarrierGripperOpen, true);
        io.SetInput(destinationSensor, false);
        AssertState(NgTransferState.WaitingForPlacement);
        io.SetInput(destinationSensor, true);
        AssertState(NgTransferState.Raising);
        io.SetInput(InputIo.NgCarrierPickupDown, false);
        io.SetInput(InputIo.NgCarrierPickupUp, true);
        AssertState(NgTransferState.Completed);
        await machine.ShutdownAsync();

        void AssertState(NgTransferState expected)
        {
            Assert.Equal(expected, move.GetState(destination, canPickUp: false));
            Assert.Equal(expected, move.GetState(destination, canPickUp: true));
        }
    }
}
