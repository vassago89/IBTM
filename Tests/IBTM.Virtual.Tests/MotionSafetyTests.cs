using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTestSupport;

namespace IBTM.Virtual.Tests;

public sealed class MotionSafetyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public async Task InvalidMoveSpeedDoesNotRetractZ(double speed)
    {
        using var motion = new VirtualMotionService(
            new MotionSettings { ZSpeed = 1_000 },
            new OperationCancellation());
        motion.Initialize();
        await HomeAsync(motion, 1_000);
        await motion.MoveAxisAsync(MotionAxis.Z, 5, 1_000);
        var moved = false;
        motion.MovingChanged += moving => moved |= moving;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => motion.MoveToXYAsync(10, 10, speed, timeout.Token));

        Assert.False(moved);
        Assert.Equal((0, 0, 5), motion.Position);
    }

    [Theory]
    [InlineData(MotionAxis.Y, 0, -292.227, 0)]
    [InlineData(MotionAxis.Z, 0, 0, -10)]
    [InlineData(MotionAxis.X, 201, 0, 0)]
    public async Task SingleAxisAdjustmentMovesOnlyTheRequestedAxis(
        MotionAxis axis,
        double x,
        double y,
        double z)
    {
        using var motion = new VirtualMotionService(new MotionSettings(), new OperationCancellation());
        motion.Initialize();

        var target = axis switch
        {
            MotionAxis.X => x,
            MotionAxis.Y => y,
            _ => z,
        };
        await motion.AdjustAxisAsync(axis, target, 10_000);

        var position = motion.Position;
        Assert.Equal(x, position.X, 6);
        Assert.Equal(y, position.Y, 6);
        Assert.Equal(z, position.Z, 6);
        Assert.False(motion.IsMoving);
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomePublishesTheCompletedState(bool horizontal)
    {
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            new OperationCancellation());
        motion.Initialize();
        var display = new MotionStatus(motion);
        var homed = false;
        motion.StateChanged += () => homed = motion.GetAxisState(MotionAxis.X).Homed
            && (!horizontal || motion.GetAxisState(MotionAxis.Y).Homed);

        var completed = horizontal
            ? await motion.HomeHorizontalAsync(100)
            : await motion.HomeAsync(MotionAxis.X, 100);

        Assert.True(completed);
        Assert.True(homed);
        display.RefreshMonitorFeedback();
        display.RefreshControlFeedback();
        Assert.True(display.Axes[MotionAxis.X].State!.Value.Homed);
        Assert.Equal(horizontal, display.Axes[MotionAxis.Y].State!.Value.Homed);
        Assert.Equal(horizontal, display.XyHomed);
        Assert.False(motion.GetAxisState(MotionAxis.Z).Homed);

        motion.SetServo(MotionAxis.X, false);
        display.RefreshMonitorFeedback();
        display.RefreshControlFeedback();
        Assert.False(display.Axes[MotionAxis.X].State?.ServoOn);
        Assert.Equal(AxisCondition.ServoOff, display.Axes[MotionAxis.X].Condition);
        Assert.True(display.Axes[MotionAxis.X].State!.Value.Homed);
    }

    [Fact]
    public async Task SlowJogAccumulatesSubPulseDistanceAndStopsOnCancellation()
    {
        const double pulseLength = 0.001;
        const double velocity = pulseLength * 20;
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            new OperationCancellation(),
            hasY: false,
            hasZ: false);
        motion.Initialize();
        using var stop = new CancellationTokenSource();

        var jog = motion.JogAsync(MotionAxis.X, velocity, stop.Token);
        Assert.False(jog.IsCompleted);
        Assert.True(
            await WaitUntilAsync(() => motion.Position.X >= pulseLength, TimeSpan.FromSeconds(1)));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => jog.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(motion.IsMoving);

        var stoppedPosition = motion.Position;
        await Task.Delay(30);
        Assert.Equal(stoppedPosition, motion.Position);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => motion.JogAsync(MotionAxis.X, velocity, stop.Token));
        Assert.False(motion.IsMoving);
    }

    [Fact]
    public async Task VirtualMotionUsesEachAxisPulseLength()
    {
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            new OperationCancellation(),
            hasZ: false,
            axisResolutionMillimeters: (0.001, 0.01, 0.001));
        motion.Initialize();
        await motion.HomeHorizontalAsync(100);
        await motion.MoveToXYAsync(1.234, 1.234, 100);
        Assert.Equal(1.234, motion.Position.X, 6);
        Assert.Equal(1.23, motion.Position.Y, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MotionAxis.X)]
    [InlineData(MotionAxis.Y)]
    public async Task AxisAndXyMovesKeepCurrentZ(MotionAxis? axis)
    {
        var settings = new MotionSettings { ZSpeed = 100 };
        using var motion = new VirtualMotionService(
            settings,
            new OperationCancellation());
        motion.Initialize();
        await HomeAsync(motion, 1_000);
        await motion.MoveAxisAsync(MotionAxis.Z, 8, 100);
        var zChanged = false;
        motion.PositionChanged += (_, _, z) => zChanged |= z != 8;

        if (axis is { } singleAxis)
            await motion.MoveAxisAsync(singleAxis, 10, 100);
        else
            await motion.MoveToXYAsync(10, 20, 100);

        var expected = axis switch
        {
            MotionAxis.X => (10, 0, 8),
            MotionAxis.Y => (0, 10, 8),
            _ => (10, 20, 8),
        };
        Assert.Equal(expected, motion.Position);
        Assert.False(zChanged);
    }

    [Fact]
    public async Task HandoffRequiresTaughtCoordinatesAndConfirmedHolding()
    {
        var operations = new OperationCancellation();
        var settings = new MotionSettings
        {
            HorizontalSpeed = 1_000,
            ZSpeed = 1_000,
        };
        var handoff = new AxisPosition { X = 10, Y = 10, Z = 8 };
        var io = CreateIo();
        using var supply = new VirtualMotionService(settings, operations);
        using var placement = new VirtualMotionService(settings, operations);
        var placementHandler = VirtualTestSupport.CreatePlacer(placement, io,
            new PcbPlacementHandlerSettings { HandoffPosition = handoff });
        var supplyHandoff = new AxisPosition { X = handoff.X, Y = handoff.Y, Z = 3 };
        var supplyHandler = VirtualTestSupport.CreateSupplier(supply, io,
            new PcbSupplySettings { HandoffPosition = supplyHandoff });

        io.Initialize();
        supply.Initialize();
        placement.Initialize();
        await Task.WhenAll(HomeAsync(supply, 1_000), HomeAsync(placement, 1_000));

        await supply.MoveToXYAsync(20, 10, settings.HorizontalSpeed);
        await supply.MoveAxisAsync(MotionAxis.Z, 8, settings.ZSpeed);
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        Assert.False(MotionServiceBase.IsAt(supplyHandler.Motion.Feedback, supplyHandoff) && supplyHandler.PcbSecured);

        await supply.MoveToXYAsync(10, 10, settings.HorizontalSpeed);
        await supply.MoveAxisAsync(MotionAxis.Z, 8, settings.ZSpeed);
        Assert.False(MotionServiceBase.IsAt(supplyHandler.Motion.Feedback, supplyHandoff));
        Assert.False(MotionServiceBase.IsAt(supplyHandler.Motion.Feedback, supplyHandoff) && supplyHandler.PcbSecured);
        await supply.MoveAxisAsync(MotionAxis.Z, 3, settings.ZSpeed);
        Assert.True(MotionServiceBase.IsAt(supplyHandler.Motion.Feedback, supplyHandoff) && supplyHandler.PcbSecured);
        io.SetInput(InputIo.PcbSupplyGripperOpen, true);
        Assert.False(MotionServiceBase.IsAt(supplyHandler.Motion.Feedback, supplyHandoff) && supplyHandler.PcbSecured);
        io.SetInput(InputIo.PcbSupplyGripperOpen, false);
        Assert.True(MotionServiceBase.IsAt(supplyHandler.Motion.Feedback, supplyHandoff) && supplyHandler.PcbSecured);

        await placement.MoveToXYAsync(10, 10, settings.HorizontalSpeed);
        await placement.MoveAxisAsync(MotionAxis.Z, 8, settings.ZSpeed);
        Assert.True(MotionServiceBase.IsAt(placementHandler.Motion.Feedback, handoff));
        Assert.False(MotionServiceBase.IsAt(placementHandler.Motion.Feedback, handoff) && placementHandler.PcbSecured);
        io.SetInputs(
            (InputIo.PcbPlacementPcbDetected, true),
            (InputIo.PcbPlacementVacuumDetected, true));
        Assert.True(MotionServiceBase.IsAt(placementHandler.Motion.Feedback, handoff) && placementHandler.PcbSecured);
    }

    [Fact]
    public async Task SupplyTravelsAtTravelZAndRotatesOnlyAtHandoffCoordinates()
    {
        var io = CreateIo();
        var settings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 1_000, ZSpeed = 1_000 },
            TravelZ = 3,
            HandoffPosition = new() { X = 20, Y = 15, Z = 7 },
        };
        using var motion = new VirtualMotionService(settings.Motion, new());
        var supply = VirtualTestSupport.CreateSupplier(motion, io, settings);
        io.Initialize();
        motion.Initialize();
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => supply.MoveAxisAsync(MotionAxis.Z, settings.TravelZ));
        await HomeAsync(motion, 1_000);
        var rotationCommands = 0;
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.PcbSupplyRotate)
                return;
            Assert.True(MotionServiceBase.IsHoldingPosition(motion, settings.HandoffPosition));
            rotationCommands++;
        };
        var xyMovedTogether = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (!motion.IsMovingHorizontal)
                return;
            Assert.Equal(settings.TravelZ, z);
            xyMovedTogether |= x > 0 && x < 20 && y > 0 && y < 15;
        };
        var handoff = new TeachingPosition(TeachingTarget.SupplyHandoff, MotionGroup.PcbSupply, TeachMode.Full);
        var pickup = new TeachingPosition(TeachingTarget.SupplyPcb1Pick, MotionGroup.PcbSupply, TeachMode.Full);
        await supply.MoveToTeachingPositionAsync(handoff, settings.HandoffPosition);
        await supply.SetRotatedAsync(true);
        Assert.True(supply.IsMoveToTeachingPositionAllowed(handoff));
        Assert.True(supply.IsMoveToTeachingPositionAllowed(pickup));
        await supply.MoveToTeachingPositionAsync(pickup, new() { X = 10, Y = 30, Z = 5 });
        Assert.Equal((10, 30, 5), motion.Position);
        await supply.PrepareHandoffAsync(default);
        Assert.Equal(PcbSupplyRotationState.Unrotated, supply.Rotation);
        Assert.True(MotionServiceBase.IsHoldingPosition(motion, settings.HandoffPosition));
        await supply.MoveFromHandoffAsync(new() { X = 10, Y = 30, Z = 5 });
        Assert.Equal((10, 30, settings.TravelZ), motion.Position);
        Assert.Equal(PcbSupplyRotationState.Rotated, supply.Rotation);
        Assert.Equal(3, rotationCommands);
        Assert.True(xyMovedTogether);

        io.SetInput(InputIo.PcbSupplyUnrotated, true);
        Assert.False(supply.IsMoveToTeachingPositionAllowed(handoff));
        Assert.False(supply.IsMoveToTeachingPositionAllowed(pickup));
        settings.TravelZ = 4;
        await supply.MoveAxisAsync(MotionAxis.Z, settings.TravelZ);
        Assert.Equal(4, motion.Position.Z);
    }

    [Theory]
    [InlineData(MotionAxis.X)]
    [InlineData(MotionAxis.Y)]
    [InlineData(MotionAxis.Z)]
    public async Task SupplyRotationRejectsAnyAxisAwayFromHandoffWithoutMoving(MotionAxis axis)
    {
        var io = CreateIo();
        var settings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 1_000, ZSpeed = 1_000 },
            TravelZ = 3,
            HandoffPosition = new() { X = 20, Y = 15, Z = 7 },
        };
        using var motion = new VirtualMotionService(settings.Motion, new());
        var supply = VirtualTestSupport.CreateSupplier(motion, io, settings);
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 1_000);
        await supply.PrepareHandoffAsync(default);
        await supply.MoveAxisAsync(axis, 1);
        var commanded = false;
        motion.MovingChanged += moving => commanded |= moving;
        io.OutputChanged += (output, on) => commanded |= output == OutputIo.PcbSupplyRotate;
        Assert.False(supply.IsRotationAllowed);
        await Assert.ThrowsAsync<MotionInterlockException>(() => supply.SetRotatedAsync(true));
        Assert.False(commanded);
    }

    [Fact]
    public async Task SupplyRotationStopsWaitingIfHandoffPositionIsLost()
    {
        var io = CreateIo();
        var settings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 1_000, ZSpeed = 1_000 },
            HandoffPosition = new() { X = 20, Y = 15, Z = 7 },
        };
        using var motion = new VirtualMotionService(settings.Motion, new());
        var supply = VirtualTestSupport.CreateSupplier(motion, io, settings);
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 1_000);
        await supply.PrepareHandoffAsync(default);
        io.AutoResponseEnabled = false;
        var rotation = supply.SetRotatedAsync(true);
        await supply.MoveAxisAsync(MotionAxis.X, 19);
        await Assert.ThrowsAsync<MotionInterlockException>(() => rotation);
        Assert.True(io.GetOutput(OutputIo.PcbSupplyRotate)); // Cancellation does not reverse the cylinder.
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MotionAxis.Z)]
    [InlineData(MotionAxis.X)]
    public async Task PlacementStandbyTeachingUsesPendingZThenXThenY(MotionAxis? cancelAfterAxis)
    {
        var settings = new PcbPlacementHandlerSettings
        {
            Motion = new MotionSettings { HorizontalSpeed = 1_000, ZSpeed = 1_000 },
            HandoffPosition = new() { X = 20, Y = 15, Z = 3 },
        };
        var io = new VirtualIoService(new PcbPlacementHandlerHardwareSettings().Outputs, new MachineOptions());
        using var motion = new VirtualMotionService(
            settings.Motion,
            new OperationCancellation());
        var placement = VirtualTestSupport.CreatePlacer(motion, io, settings);
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 1_000);
        io.SetInputs(
            (InputIo.PcbPlacementHandlerUp, true),
            (InputIo.PcbPlacementHandlerDown, false));
        await motion.MoveAxisAsync(MotionAxis.Z, 9, 1_000);

        var standby = new TeachingPosition(TeachingTarget.PlacementHandoff, MotionGroup.PcbPlacementHandler, TeachMode.Full);
        var pending = new AxisPosition { X = 30, Y = 25, Z = 7 };
        using var stop = new CancellationTokenSource();
        var positions = new List<(double X, double Y, double Z)>();
        motion.PositionChanged += (x, y, z) =>
        {
            positions.Add((x, y, z));
            if ((cancelAfterAxis == MotionAxis.Z && x == 0 && y == 0 && z == pending.Z)
                || (cancelAfterAxis == MotionAxis.X && x == pending.X && y == 0))
                stop.Cancel();
        };

        if (cancelAfterAxis is not null)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => placement.MoveToTeachingPositionAsync(standby, pending, stop.Token));
            var expectedX = cancelAfterAxis == MotionAxis.X ? pending.X : 0;
            Assert.Equal((expectedX, 0, pending.Z), motion.Position);
        }
        else
        {
            await placement.MoveToTeachingPositionAsync(standby, pending, stop.Token);
            Assert.Equal((pending.X, pending.Y, pending.Z), motion.Position);
        }

        Assert.NotEmpty(positions);
        Assert.All(positions, position =>
        {
            Assert.InRange(position.Z, pending.Z, 9);
            if (position.X != 0 || position.Y != 0)
                Assert.Equal(pending.Z, position.Z);
            if (position.Y != 0)
                Assert.Equal(pending.X, position.X);
        });
        Assert.False(motion.IsMoving);
        Assert.Equal((20, 15, 3),
            (settings.HandoffPosition.X, settings.HandoffPosition.Y, settings.HandoffPosition.Z));
    }

    [Theory]
    [InlineData(TeachingTarget.HeatSink1PcbPlacement, false)]
    [InlineData(TeachingTarget.HeatSink2PcbPlacement, false)]
    [InlineData(TeachingTarget.HeatSink1PcbPlacement, true)]
    public async Task PlacementHeatSinkTeachingMovesYThenXBeforeTargetZ(
        TeachingTarget target,
        bool cancelAfterY)
    {
        var settings = new PcbPlacementHandlerSettings
        {
            Motion = new MotionSettings { HorizontalSpeed = 1_000, ZSpeed = 1_000 },
            HandoffPosition = new() { Z = 3 },
        };
        var io = new VirtualIoService(new PcbPlacementHandlerHardwareSettings().Outputs, new MachineOptions());
        using var motion = new VirtualMotionService(
            settings.Motion,
            new OperationCancellation());
        var placement = VirtualTestSupport.CreatePlacer(motion, io, settings);
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 1_000);
        io.SetInputs(
            (InputIo.PcbPlacementHandlerUp, true),
            (InputIo.PcbPlacementHandlerDown, false));
        await motion.MoveAxisAsync(MotionAxis.Z, 9, 1_000);

        var point = new TeachingPosition(target, MotionGroup.PcbPlacementHandler, TeachMode.Full);
        var destination = new AxisPosition { X = 30, Y = 25, Z = 7 };
        using var stop = new CancellationTokenSource();
        var positions = new List<(double X, double Y, double Z)>();
        motion.PositionChanged += (x, y, z) =>
        {
            positions.Add((x, y, z));
            if (cancelAfterY && y == destination.Y)
                stop.Cancel();
        };

        if (cancelAfterY)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => placement.MoveToTeachingPositionAsync(point, destination, stop.Token));
            Assert.Equal((0, destination.Y, settings.HandoffPosition.Z), motion.Position);
        }
        else
        {
            await placement.MoveToTeachingPositionAsync(point, destination, stop.Token);
            Assert.Equal((destination.X, destination.Y, destination.Z), motion.Position);
        }

        Assert.NotEmpty(positions);
        Assert.All(positions, position =>
        {
            if (position.X != 0)
                Assert.Equal(destination.Y, position.Y);
            if ((position.X != 0 || position.Y != 0)
                && (position.X != destination.X || position.Y != destination.Y))
                Assert.Equal(settings.HandoffPosition.Z, position.Z);
        });
        Assert.False(motion.IsMoving);
    }

    private static VirtualIoService CreateIo()
    {
        return new(new PcbSupplyHardwareSettings().Outputs, new MachineOptions());
    }
}
