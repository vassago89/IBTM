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
    [InlineData(false)]
    [InlineData(true)]
    public async Task NgTransferStopsBeforeLoweringAfterGripFeedbackLoss(bool opens)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        settings.NgCarrierTransfer.Speed = 200;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var move = services.GetRequiredService<NgCarrierTransfer>();
        var gantry = services.GetRequiredService<NgCarrierTransfer>();
        var pickup = services.GetRequiredService<NgCarrierTransfer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await services.GetRequiredService<InspectionWork>().Station.SeatAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.PickingCarrier, timeout.Token);
        var lost = false;
        var lowered = false;
        gantry.Feedback.PositionChanged += (x, y, z) =>
        {
            if (!lost && gantry.Feedback.IsMoving && x > 30)
            {
                lost = true;
                io.SetInputs((InputIo.NgCarrierGripperClosed, false), (InputIo.NgCarrierGripperOpen, opens));
            }
        };
        io.OutputChanged += (output, on) => lowered |= output == OutputIo.NgCarrierPickupDown && on;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => move.ExecuteAsync(
                NgTransferDestination.Shuttle, NgTransferState.PlacingCarrier, timeout.Token));
            Assert.True(lost);
            Assert.False(lowered);
            Assert.False(gantry.Feedback.IsMoving);
            Assert.True(pickup.IsTransferPending);
            var restarted = false;
            io.OutputChanged += (output, on) => restarted |= output is OutputIo.NgCarrierGripperClose
                or OutputIo.NgCarrierPickupDown;
            gantry.Feedback.MovingChanged += moving => restarted |= moving;
            await Assert.ThrowsAsync<InvalidOperationException>(() => move.ExecuteAsync(
                NgTransferDestination.Shuttle,
                move.GetState(NgTransferDestination.Shuttle, canPickUp: true), timeout.Token));
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
        settings.Units.ShootingBoltFeeder = true;
        await using var services = CreateServices(settings);
        services.GetRequiredService<RecipeManager>().Current.Pcb.BoltPoints =
            [new() { Number = 1, Head = FasteningHead.Shooting, X = 0, Y = 0 }];
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningWork>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
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
            await station.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(4));
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
        var handler = services.GetRequiredService<PcbSupplier>();
        var motion = handler.Feedback;
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
    public async Task ReverseNgPickupStaysAtShuttleAndClearsStationOnlyWithFirstFov(bool hasFov)
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var move = services.GetRequiredService<NgCarrierTransfer>();
        var settings = services.GetRequiredService<NgCarrierTransferSettings>();
        var gantry = services.GetRequiredService<NgCarrierTransfer>();
        var pickup = services.GetRequiredService<NgCarrierTransfer>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        // Seed the shuttle through a real virtual transfer so the carrier retains
        // its heat-sink payload when it returns to Station 3.
        io.SetInputs(
            (InputIo.InspectionHeatSink1Present, true),
            (InputIo.InspectionHeatSink2Present, true));
        await services.GetRequiredService<InspectionWork>().Station.SeatAsync(CancellationToken.None);
        await gantry.MoveToAsync(settings.GetCarrierPickupPosition()!, 10_000);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        using var transferTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await move.RunToAsync(NgTransferDestination.Shuttle, transferTimeout.Token);
        feedback.AxisMoves.Clear();
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

        await move.ReturnToStationAsync(transferTimeout.Token).WaitAsync(TimeSpan.FromSeconds(3));

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
        var pickupPosition = settings.GetCarrierPickupPosition()!;
        settings.Speed = 100;
        using var cancellation = new CancellationTokenSource();
        void StopDuringFovMove(double x, double y, double z)
        {
            if (x > pickupPosition.X + 1 && x < firstFov.X
                && y > pickupPosition.Y + 1 && y < firstFov.Y)
                cancellation.Cancel();
        }
        gantry.Feedback.PositionChanged += StopDuringFovMove;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => move.ClearStationAsync(firstFov, cancellation.Token));
        gantry.Feedback.PositionChanged -= StopDuringFovMove;
        Assert.Empty(feedback.AxisMoves);
        var stopped = gantry.Feedback.GetPosition();
        Assert.InRange(stopped.X, pickupPosition.X + 0.01, firstFov.X - 0.01);
        Assert.InRange(stopped.Y, pickupPosition.Y + 0.01, firstFov.Y - 0.01);

        feedback.AxisMoves.Clear();
        await move.ClearStationAsync(firstFov, CancellationToken.None);
        Assert.Empty(feedback.AxisMoves);
        Assert.True(gantry.IsAt(firstFov));
        Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.False(pickup.CarrierDetected);
    }

    [Fact]
    public async Task NgPickupMovesXyTogetherAndResumesBeforeGripping()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var move = services.GetRequiredService<NgCarrierTransfer>();
        var settings = services.GetRequiredService<NgCarrierTransferSettings>();
        var gantry = services.GetRequiredService<NgCarrierTransfer>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 50, Y = 60 }, 10_000);

        settings.PickupSafeX = null;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.PickingCarrier, CancellationToken.None)!);
        Assert.Empty(feedback.AxisMoves);
        Assert.Equal((50, 60, 0), gantry.Feedback.GetPosition());

        settings.PickupSafeX = 5;
        settings.Speed = 100;
        var pickupPosition = settings.GetCarrierPickupPosition()!;
        using var cancellation = new CancellationTokenSource();
        void StopDuringPickupMove(double x, double y, double z)
        {
            if (x > pickupPosition.X && x < 49
                && y > pickupPosition.Y && y < 59)
                cancellation.Cancel();
        }
        gantry.Feedback.PositionChanged += StopDuringPickupMove;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.PickingCarrier, cancellation.Token)!);
        gantry.Feedback.PositionChanged -= StopDuringPickupMove;
        Assert.Empty(feedback.AxisMoves);
        var stopped = gantry.Feedback.GetPosition();
        Assert.InRange(stopped.X, pickupPosition.X + 0.01, 49.99);
        Assert.InRange(stopped.Y, pickupPosition.Y + 0.01, 59.99);
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.False(io.GetInput(InputIo.NgCarrierDetected));

        feedback.AxisMoves.Clear();
        Assert.False(await move.ExecuteAsync(
            NgTransferDestination.Shuttle, NgTransferState.PickingCarrier, CancellationToken.None));
        Assert.Empty(feedback.AxisMoves);
        Assert.True(gantry.IsAt(pickupPosition));
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.NotEqual(settings.CarrierPickupPosition.X, gantry.Feedback.GetPosition().X);

        await services.GetRequiredService<InspectionWork>().Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        Assert.Equal(NgTransferState.PickingCarrier, move.GetState(NgTransferDestination.Shuttle, canPickUp: true));
        await move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.PickingCarrier, CancellationToken.None)!;
        Assert.Equal(NgTransferState.PlacingCarrier, move.GetState(NgTransferDestination.Shuttle, canPickUp: true));
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        Assert.Equal(5, gantry.Feedback.GetPosition().X);
        Assert.Empty(feedback.AxisMoves);

        feedback.AxisMoves.Clear();
        await move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.PlacingCarrier, CancellationToken.None)!;
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
        var handler = services.GetRequiredService<PcbPlacer>();
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
            InputIo.PcbPlacementHandlerUnrotated,
            InputIo.PcbPlacementHandlerDown,
            InputIo.PcbPlacementIpmUp,
        })
            io.SetInput(input, true);
        foreach (var input in new[]
        {
            InputIo.PcbPlacementBackupPlateDown,
            InputIo.PcbPlacementStopperUp,
            InputIo.PcbPlacementIpmGripperClosed,
            InputIo.PcbPlacementHandlerRotated,
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
            Assert.Equal(PcbPlacementState.PlacingPcb, placer.State);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => placer.PlaceAsync(HeatSinkSlot.HeatSink1, CancellationToken.None)!);
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
        var handler = services.GetRequiredService<PcbPlacer>();
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
            InputIo.PcbPlacementHandlerUnrotated,
            InputIo.PcbPlacementIpmDown
        })
            io.SetInput(input, true);
        foreach (var input in new[]
        {
            InputIo.PcbPlacementBackupPlateDown,
            InputIo.PcbPlacementStopperUp,
            InputIo.PcbPlacementIpmGripperOpen,
            InputIo.PcbPlacementHandlerRotated,
            InputIo.PcbPlacementIpmUp
        })
            io.SetInput(input, false);
        // XY alone is not a placement position, even with PCB detection and vacuum OFF.
        Assert.DoesNotContain(
            placer.State,
            new[] { PcbPlacementState.PlacingPcb });
        await handler.MoveAxisAsync(MotionAxis.Z, 10);
        Assert.DoesNotContain(
            placer.State,
            new[] { PcbPlacementState.PlacingPcb });
        io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        io.SetInput(InputIo.PcbPlacementHandlerDown, true);
        io.SetOutput(OutputIo.PcbPlacementHandlerDown, true);

        var outputs = new List<(OutputIo, bool)>();
        using var closing = new CancellationTokenSource();
        using var pressing = new CancellationTokenSource();
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementHandlerDown)
            {
                io.SetInputs((InputIo.PcbPlacementHandlerDown, on), (InputIo.PcbPlacementHandlerUp, !on));
                return;
            }
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

        Assert.Equal(PcbPlacementState.PlacingPcb, placer.State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => placer.PlaceAsync(HeatSinkSlot.HeatSink1, closing.Token)!);
        Assert.Equal(PlacementGripperState.Closed, handler.IpmGripper);
        Assert.Equal(PcbPlacementState.PlacingPcb, placer.State);
        Assert.Empty(work.Assemblies);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => placer.PlaceAsync(HeatSinkSlot.HeatSink1, pressing.Token)!);
        Assert.Equal(PlacementCylinderState.Between, handler.IpmLift);
        Assert.Equal(PcbPlacementState.PlacingPcb, placer.State);
        Assert.Empty(work.Assemblies);
        io.SetInput(InputIo.PcbPlacementIpmDown, true);
        Assert.Equal(PcbPlacementState.PlacingPcb, placer.State);
        if (replaceCarrier)
        {
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            Assert.Equal(PcbPlacementState.PlacingPcb, placer.State);
            Assert.Empty(work.Assemblies);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => placer.PlaceAsync(HeatSinkSlot.HeatSink1, pressing.Token)!);
            Assert.Empty(work.Assemblies);
            await placer.PlaceAsync(HeatSinkSlot.HeatSink1, CancellationToken.None)!;
            Assert.Single(work.Assemblies);
        }
        var expected = new List<(OutputIo, bool)>
        {
            (OutputIo.PcbPlacementIpmGripperClose, false),
            (OutputIo.PcbPlacementIpmDown, false),
            (OutputIo.PcbPlacementIpmGripperClose, true),
            (OutputIo.PcbPlacementIpmDown, true),
        };
        if (!replaceCarrier)
        {
            expected.Add((OutputIo.PcbPlacementIpmDown, false));
            Assert.Equal(PlacementCylinderState.Up, handler.Lift);
            Assert.True(handler.IsAtHorizontalZ());
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
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var move = services.GetRequiredService<NgCarrierTransfer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await services.GetRequiredService<InspectionWork>().Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        // Establish pickup ownership through the real sequence, independently of presence DI.
        await move.ExecuteAsync(destination, NgTransferState.PickingCarrier,
            CancellationToken.None, allowEmpty: true);
        await services.GetRequiredService<NgCarrierTransfer>()
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
        AssertState(NgTransferState.PlacingCarrier);
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
            Assert.Equal(NgTransferState.GrippingCarrier,
                move.GetState(destination, canPickUp: true, holdAtDestination: true));
            io.SetInput(InputIo.NgCarrierDetected, true);
            io.SetInput(InputIo.NgCarrierPickupDown, false);
            io.SetInput(InputIo.NgCarrierGripperClosed, false);
            io.SetInput(InputIo.NgCarrierGripperOpen, true);
            // Unexpected opening above an unsupported destination must retain the failed transfer.
            Assert.Equal(NgTransferState.GrippingCarrier,
                move.GetState(destination, canPickUp: true, holdAtDestination: true));
            io.SetInput(InputIo.NgCarrierPickupDown, true);
            io.SetInput(InputIo.NgCarrierGripperClosed, true);
            io.SetInput(InputIo.NgCarrierGripperOpen, false);
            io.SetInput(InputIo.NgShuttleUp, true);
            var gripperOutput = io.GetOutput(OutputIo.NgCarrierGripperClose);
            Assert.False(await move.ExecuteAsync(
                destination, NgTransferState.HoldingAtDestination, CancellationToken.None));
            Assert.Equal(gripperOutput, io.GetOutput(OutputIo.NgCarrierGripperClose));
        }
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        AssertState(NgTransferState.Opening);
        io.SetInput(InputIo.NgCarrierGripperOpen, true);
        var pickup = services.GetRequiredService<NgCarrierTransfer>();
        Assert.True(pickup.IsTransferPending);
        await move.ExecuteAsync(destination, NgTransferState.Opening, CancellationToken.None);
        Assert.False(pickup.IsTransferPending);
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
