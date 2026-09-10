using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReverseNgPickupStaysAtShuttleAndClearsStationOnlyWithFirstFov(bool hasFov)
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var move = services.GetRequiredService<NgCarrierMove>();
        var settings = services.GetRequiredService<NgCarrierTransferSettings>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var pickup = services.GetRequiredService<NgCarrierTransfer>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(settings.ShuttlePlacePosition, 10_000);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleUp, true);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        settings.PickupSafeX = null;
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
        Assert.True(io.GetInput(InputIo.InspectionCarrierPresent));
        Assert.False(io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.True(pickup.IsRaised);
        Assert.Equal(NgTransferGripperState.Open, pickup.Gripper);
        Assert.True(gantry.IsAt(settings.CarrierPickupPosition));
        if (!hasFov)
        {
            await move.ClearStationAsync(null, CancellationToken.None);
            Assert.Empty(feedback.AxisMoves);
            Assert.True(gantry.IsAt(settings.CarrierPickupPosition));
            return;
        }

        var firstFov = new AxisPosition { X = 30, Y = 40 };
        settings.PickupSafeX = 5;
        using var cancellation = new CancellationTokenSource();
        void StopAtSafeX(double x, double y, double z)
        {
            if (x == settings.PickupSafeX)
                cancellation.Cancel();
        }
        gantry.Feedback.PositionChanged += StopAtSafeX;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => move.ClearStationAsync(firstFov, cancellation.Token));
        gantry.Feedback.PositionChanged -= StopAtSafeX;
        Assert.Equal(new[] { (MotionAxis.X, 5d) }, feedback.AxisMoves);
        Assert.Equal(settings.CarrierPickupPosition.Y, gantry.Feedback.GetPosition().Y);

        feedback.AxisMoves.Clear();
        await move.ClearStationAsync(firstFov, CancellationToken.None);
        Assert.Equal(
            new[] { (MotionAxis.X, 5d), (MotionAxis.Y, 40d), (MotionAxis.X, 30d) },
            feedback.AxisMoves);
        Assert.True(gantry.IsAt(firstFov));
        Assert.True(io.GetInput(InputIo.InspectionCarrierPresent));
        Assert.False(pickup.CarrierDetected);
    }

    [Fact]
    public async Task NgPickupApproachUsesSafeXThenYThenXAndCancelsBetweenAxes()
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var move = services.GetRequiredService<NgCarrierMove>();
        var settings = services.GetRequiredService<NgCarrierTransferSettings>();
        var gantry = services.GetRequiredService<InspectionGantry>();
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
            new[] { (MotionAxis.X, 5d), (MotionAxis.Y, settings.CarrierPickupPosition.Y), (MotionAxis.X, settings.CarrierPickupPosition.X) },
            feedback.AxisMoves);

        feedback.AxisMoves.Clear();
        await move.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.MovingToDestination, CancellationToken.None)!;
        Assert.Empty(feedback.AxisMoves);
        Assert.True(gantry.IsAt(settings.ShuttlePlacePosition));
    }

    [Trait("Category", "MachineFlow")]
    [Fact]
    public async Task PlacementOpensAndRaisesIpmBeforePressingAndResumesWithoutReopening()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.PcbSupply = true;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var handler = services.GetRequiredService<PcbPlacementHandler>();
        var placer = services.GetRequiredService<PcbPlacer>();
        var work = services.GetRequiredService<PcbPlacementWork>();
        var recipe = services.GetRequiredService<Recipe>().PcbPlacement;
        recipe.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 10 };
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await handler.MoveToXYAsync(20, 100);
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.PcbPlacementIpmGripperClose, true);
        io.SetOutput(OutputIo.PcbPlacementIpmDown, true);
        foreach (var input in new[]
        {
            InputIo.PcbPlacementCarrierPresent,
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
            placer.State(recipe),
            new[] { PcbPlacementState.PressingPcb, PcbPlacementState.RecordingPlacement });
        await handler.MoveAxisAsync(MotionAxis.Z, 10);
        Assert.DoesNotContain(
            placer.State(recipe),
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
            io.SetInput(on ? feedback.OffInput : feedback.OnInput, false);
            if (output == OutputIo.PcbPlacementIpmDown && on)
            {
                pressing.Cancel(); // Stop between Up and Down feedback.
                return;
            }

            io.SetInput(on ? feedback.OnInput : feedback.OffInput, true);
            if (output == OutputIo.PcbPlacementIpmGripperClose && on)
                closing.Cancel();
        };

        Assert.Equal(PcbPlacementState.OpeningGripper, placer.State(recipe));
        await placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, CancellationToken.None)!;
        Assert.Equal(PcbPlacementState.RaisingIpm, placer.State(recipe));
        await placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, CancellationToken.None)!;
        Assert.Equal(PcbPlacementState.PressingPcb, placer.State(recipe));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, closing.Token)!);
        Assert.Equal(PlacementGripperState.Closed, handler.IpmGripper);
        Assert.Equal(PcbPlacementState.PressingPcb, placer.State(recipe));
        Assert.Empty(work.Assemblies);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, pressing.Token)!);
        Assert.Equal(PlacementCylinderState.Between, handler.IpmLift);
        Assert.Equal(PcbPlacementState.PressingPcb, placer.State(recipe));
        Assert.Empty(work.Assemblies);
        io.SetInput(InputIo.PcbPlacementIpmDown, true);
        Assert.Equal(PcbPlacementState.RecordingPlacement, placer.State(recipe));
        await placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, CancellationToken.None)!;
        Assert.Single(work.Assemblies);
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
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var move = services.GetRequiredService<NgCarrierMove>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await services.GetRequiredService<InspectionWork>().Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleUp, true);
        await services.GetRequiredService<InspectionGantry>()
            .MoveToAsync(
                destination == NgTransferDestination.Station
                    ? settings.NgCarrierTransfer.CarrierPickupPosition
                    : settings.NgCarrierTransfer.ShuttlePlacePosition,
                1_000);
        io.AutoResponseEnabled = false;
        var destinationSensor = destination == NgTransferDestination.Station
            ? InputIo.InspectionCarrierPresent
            : InputIo.NgShuttleCarrierDetected;
        io.SetInput(InputIo.NgCarrierDetected, true);
        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, true);
        Assert.Equal(
            NgTransferState.WaitingForDestination,
            move.State(destination, canPickUp: true, canReceive: false));
        io.SetInput(destinationSensor, true);
        AssertState(NgTransferState.WaitingForDestination);
        // The descending held carrier can enter the support sensor before Down.
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        AssertState(NgTransferState.LoweringAtDestination);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        AssertState(NgTransferState.Opening);
        if (destination == NgTransferDestination.Shuttle)
        {
            Assert.Equal(NgTransferState.HoldingAtDestination,
                move.State(destination, canPickUp: true, holdAtDestination: true));
            var gripperOutput = io.GetOutput(OutputIo.NgCarrierGripperOpen);
            Assert.Null(move.ExecuteAsync(
                destination, NgTransferState.HoldingAtDestination, CancellationToken.None));
            Assert.Equal(gripperOutput, io.GetOutput(OutputIo.NgCarrierGripperOpen));
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
            Assert.Equal(expected, move.State(destination, canPickUp: false));
            Assert.Equal(expected, move.State(destination, canPickUp: true));
        }
    }

}
