using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

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
            new OperationCancellation(),
            horizontalZ: () => 0);
        motion.Initialize();
        await HomeAsync(motion, 1_000);
        await motion.MoveAxisAsync(MotionAxis.Z, 5, 1_000);
        var moved = false;
        motion.MovingChanged += moving => moved |= moving;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => motion.MoveToXYAsync(10, 10, speed, timeout.Token));

        Assert.False(moved);
        Assert.Equal((0, 0, 5), motion.GetPosition());
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

        var position = motion.GetPosition();
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
        Assert.False(display.Axes[MotionAxis.X].ServoOn);
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
            await WaitUntilAsync(() => motion.GetPosition().X >= pulseLength, TimeSpan.FromSeconds(1)));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => jog.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(motion.IsMoving);

        var stoppedPosition = motion.GetPosition();
        await Task.Delay(30);
        Assert.Equal(stoppedPosition, motion.GetPosition());
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
        Assert.Equal(1.234, motion.GetPosition().X, 6);
        Assert.Equal(1.23, motion.GetPosition().Y, 6);
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
            new OperationCancellation(),
            horizontalZ: () => -5);
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
        Assert.Equal(expected, motion.GetPosition());
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
        using var supply = Motion(settings, operations);
        using var placement = Motion(settings, operations);
        var placementHandler = new PcbPlacementHandler(placement, io,
            new PcbPlacementHandlerSettings { HandoffPosition = handoff });
        var supplyHandler = new PcbSupplyHandler(supply, io,
            new PcbSupplySettings { HandoffPosition = new() { X = handoff.X, Y = handoff.Y, Z = 3 } });

        io.Initialize();
        supply.Initialize();
        placement.Initialize();
        await Task.WhenAll(HomeAsync(supply, 1_000), HomeAsync(placement, 1_000));

        await supply.MoveToAsync(20, 10, 8);
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        Assert.False(supplyHandler.IsAtHandoff() && supplyHandler.PcbSecured);

        await supply.MoveToAsync(10, 10, 8);
        Assert.False(supplyHandler.IsAtHandoff());
        Assert.False(supplyHandler.IsAtHandoff() && supplyHandler.PcbSecured);
        await supply.MoveAxisAsync(MotionAxis.Z, 3, settings.ZSpeed);
        Assert.True(supplyHandler.IsAtHandoff() && supplyHandler.PcbSecured);
        io.SetInput(InputIo.PcbSupplyGripperOpen, true);
        Assert.False(supplyHandler.IsAtHandoff() && supplyHandler.PcbSecured);
        io.SetInput(InputIo.PcbSupplyGripperOpen, false);
        Assert.True(supplyHandler.IsAtHandoff() && supplyHandler.PcbSecured);

        await placement.MoveToAsync(10, 10, 8);
        Assert.True(placementHandler.IsAtHandoff());
        Assert.False(placementHandler.IsAtHandoff() && placementHandler.PcbSecured);
        io.SetInputs(
            (InputIo.PcbPlacementPcbDetected, true),
            (InputIo.PcbPlacementVacuumDetected, true),
            (InputIo.PcbPlacementIpmGripperOpen, false),
            (InputIo.PcbPlacementIpmGripperClosed, true));
        Assert.True(placementHandler.IsAtHandoff() && placementHandler.PcbSecured);
    }

    [Fact]
    public async Task SupplyUsesRotatedPickupAndUnrotatedHandoffTravelHeights()
    {
        var io = CreateIo();
        var settings = new PcbSupplySettings
        {
            Motion = new MotionSettings
            {
                HorizontalSpeed = 100,
                ZSpeed = 1_000,
            },
            RotationZ = 3,
            HandoffPosition = new AxisPosition
            {
                X = 20,
                Y = 15,
                Z = 7,
            },
        };
        using var motion = new VirtualMotionService(
            settings.Motion,
            new OperationCancellation(),
            horizontalZ: () => settings.RotationZ);
        var supply = new PcbSupplyHandler(
            motion,
            io,
            settings);

        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 1_000);

        var points = settings.GetTeachingPositions(new());
        var handoff = Array.Find(points, point => point.Target == TeachingTarget.SupplyHandoff)!;
        var pickups = Array.FindAll(points,
            point => point.Target is TeachingTarget.SupplyPcb1Pick or TeachingTarget.SupplyPcb2Pick);
        await supply.SetRotatedAsync(true);
        Assert.All(pickups, point => Assert.True(supply.IsMoveToTeachingPositionAllowed(point)));
        Assert.False(supply.IsMoveToTeachingPositionAllowed(handoff));
        await Assert.ThrowsAsync<MotionInterlockException>(() => supply.MoveToHandoffAsync(default));
        await supply.MoveAxisAsync(MotionAxis.Z, 5);
        await supply.MoveAxisAsync(MotionAxis.X, 0);
        Assert.Equal(5, motion.GetPosition().Z);

        var xyMovedTogether = false;
        var yMovedAtHandoff = false;
        var movedAtWrongZ = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (x > MotionService.PositionToleranceMillimeters
                && x < 20 - MotionService.PositionToleranceMillimeters
                && y > MotionService.PositionToleranceMillimeters
                && System.Math.Abs(y - 15) > MotionService.PositionToleranceMillimeters)
            {
                xyMovedTogether = true;
            }

            yMovedAtHandoff |= x is >= 10 and <= 30
                && Math.Abs(y - 15) > MotionService.PositionToleranceMillimeters;
            movedAtWrongZ |= motion.IsMovingHorizontal
                && Math.Abs(z - settings.HandoffPosition.Z) > MotionService.PositionToleranceMillimeters;
        };

        await supply.SetRotatedAsync(false);
        Assert.True(supply.IsMoveToTeachingPositionAllowed(handoff));
        Assert.All(pickups, point => Assert.False(supply.IsMoveToTeachingPositionAllowed(point)));
        Assert.Equal(settings.RotationZ, motion.GetPosition().Z);
        await supply.MoveAxisAsync(MotionAxis.Z, 5);
        await supply.MoveToTeachingPositionAsync(handoff, new() { X = 20, Y = 15, Z = 7 });

        Assert.True(xyMovedTogether);
        Assert.False(movedAtWrongZ);
        Assert.Equal((20, 15, settings.HandoffPosition.Z), motion.GetPosition());
        Assert.Equal(TeachMode.Full, handoff.Mode);

        await supply.MoveAxisAsync(MotionAxis.Y, 14);
        await supply.MoveAxisAsync(MotionAxis.Z, 6);
        Assert.Equal((20, 14, 6), motion.GetPosition());

        // XY departure keeps handoff Z until the handler is outside.
        Assert.True(supply.IsMoveToTeachingPositionAllowed(handoff));
        await supply.MoveToHandoffAsync(default, new() { X = 0, Y = 0, Z = settings.HandoffPosition.Z });
        Assert.True(yMovedAtHandoff);
        Assert.False(movedAtWrongZ);
        Assert.Equal((0, 0, settings.HandoffPosition.Z), motion.GetPosition());

        io.SetInput(InputIo.PcbSupplyRotated, true); // Both inputs ON is unknown.
        Assert.False(supply.IsMoveToTeachingPositionAllowed(handoff));
        Assert.All(pickups, point => Assert.False(supply.IsMoveToTeachingPositionAllowed(point)));
    }

    private static VirtualIoService CreateIo()
    {
        return new(new PcbSupplyHardwareSettings().Outputs, new MachineOptions());
    }
}
