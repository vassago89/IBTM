using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
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
            new OperationCancellation(),
            hasZ: false);
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
    public async Task MotionRetractsZBeforeXyMovement(MotionAxis? axis)
    {
        var settings = new MotionSettings { ZSpeed = 100 };
        using var motion = new VirtualMotionService(
            settings,
            new OperationCancellation(),
            horizontalZ: () => -5);
        motion.Initialize();
        await HomeAsync(motion, 1_000);
        await motion.MoveAxisAsync(MotionAxis.Z, 8, 100);
        var movedXyBeforeZClear = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if ((x != 0 || y != 0)
                && System.Math.Abs(z + 5) > MotionService.PositionToleranceMillimeters)
            {
                movedXyBeforeZClear = true;
            }
        };

        if (axis is { } singleAxis)
            await motion.MoveAxisAsync(singleAxis, 10, 100);
        else
            await motion.MoveToXYAsync(10, 20, 100);

        var expected = axis switch
        {
            MotionAxis.X => (10, 0, -5),
            MotionAxis.Y => (0, 10, -5),
            _ => (10, 20, -5),
        };
        Assert.Equal(expected, motion.GetPosition());
        Assert.False(movedXyBeforeZClear);
    }

    [Fact]
    public async Task BufferAllowsOnlyTheTaughtHandoffOverlap()
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
        var placementHandler = new PcbPlacementHandler(placement, io, new PcbPlacementHandlerSettings());
        var buffer = new BufferStage(
            new PcbBufferSettings
            {
                SupplyBoundary1 = 5,
                SupplyBoundary2 = 50,
                PlacementBoundary1 = new AxisPosition { X = 5, Y = 5 },
                PlacementBoundary2 = new AxisPosition { X = 30, Y = 12 },
            },
            new PcbSupplyHandler(supply, io, new PcbSupplySettings(), new PcbBufferSettings()),
            placementHandler,
            new MotionStatus(supply),
            placementHandler.Motion,
            new XyPosition { X = handoff.X, Y = handoff.Y },
            handoff,
            () => 0);

        io.Initialize();
        supply.Initialize();
        placement.Initialize();
        await Task.WhenAll(HomeAsync(supply, 1_000), HomeAsync(placement, 1_000));

        io.SetInputs(
            (InputIo.PcbPlacementHandlerUp, true),
            (InputIo.PcbPlacementHandlerDown, false));
        await placement.MoveToAsync(10, 10, 0);
        Assert.True(buffer.CanEnterSupply());
        await placement.MoveAxisAsync(MotionAxis.Z, 8, settings.ZSpeed);
        Assert.True(buffer.CanEnterSupply());
        // Neither an output command nor ambiguous paired inputs prove the lift is Up.
        io.SetInput(InputIo.PcbPlacementHandlerDown, true);
        Assert.False(buffer.CanEnterSupply());
        io.SetInput(InputIo.PcbPlacementHandlerDown, false);
        await placement.MoveAxisAsync(MotionAxis.Z, 7, settings.ZSpeed);
        Assert.True(buffer.CanEnterSupply());
        await placement.MoveToAsync(0, 0, 0);

        await supply.MoveToAsync(20, 10, 8);
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        Assert.False(buffer.CanEnterPlacement());

        await placement.MoveToAsync(15, 10, 8);
        Assert.False(buffer.HasConflict());
        io.SetInputs(
            (InputIo.PcbPlacementHandlerUp, false),
            (InputIo.PcbPlacementHandlerDown, true));
        Assert.True(buffer.HasConflict());
        await placement.MoveToAsync(0, 0, 0);

        await supply.MoveToAsync(10, 10, 8);
        Assert.False(buffer.IsSupplyAtHandoff());
        Assert.False(buffer.CanEnterPlacement());
        await supply.MoveAxisAsync(MotionAxis.Z, 0, settings.ZSpeed);
        Assert.True(buffer.CanEnterPlacement());

        await placement.MoveToAsync(10, 10, 8);
        Assert.False(buffer.HasConflict());
        io.SetInputs(
            (InputIo.PcbPlacementHandlerUp, true),
            (InputIo.PcbPlacementHandlerDown, false));
        // Leaving the shared zone is position feedback, even while jogging continues.
        using var stop = new CancellationTokenSource();
        var outside = buffer.WaitForSupplyOutsideAsync(stop.Token);
        Assert.False(outside.IsCompleted);
        var jog = supply.JogAsync(MotionAxis.X, -100, stop.Token, atCurrentHeight: true);
        try
        {
            Assert.True(await WaitUntilAsync(() => buffer.IsSupplyOutside(), TimeSpan.FromSeconds(1)));
            await outside.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(supply.IsMoving);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jog);
        }
    }

    [Fact]
    public async Task SupplyMovesXyTogetherAtTransportHeightAfterRotation()
    {
        var io = CreateIo();
        var settings = new PcbSupplySettings
        {
            Motion = new MotionSettings
            {
                HorizontalSpeed = 100,
                ZSpeed = 1_000,
            },
            RotationZ = 0,
            BufferHandoffPosition = new XyPosition
            {
                X = 20,
                Y = 15,
            },
        };
        using var motion = new VirtualMotionService(
            settings.Motion,
            new OperationCancellation(),
            horizontalZ: () => settings.RotationZ);
        var supply = new PcbSupplyHandler(
            motion,
            io,
            settings,
            new PcbBufferSettings { SupplyBoundary1 = 10, SupplyBoundary2 = 30, });

        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 1_000);

        var xyMovedTogether = false;
        var yMovedInsideBuffer = false;
        var movedBelowTransportZ = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (x > MotionService.PositionToleranceMillimeters
                && x < 20 - MotionService.PositionToleranceMillimeters
                && y > MotionService.PositionToleranceMillimeters
                && System.Math.Abs(y - 15) > MotionService.PositionToleranceMillimeters)
            {
                xyMovedTogether = true;
            }

            yMovedInsideBuffer |= x is >= 10 and <= 30
                && Math.Abs(y - 15) > MotionService.PositionToleranceMillimeters;
            movedBelowTransportZ |= x > MotionService.PositionToleranceMillimeters
                && Math.Abs(z - settings.RotationZ) > MotionService.PositionToleranceMillimeters;
        };

        await Assert.ThrowsAsync<MotionInterlockException>(() => supply.MoveToHandoffAsync(default));

        await supply.SetRotatedAsync(true);
        await supply.MoveAxisAsync(MotionAxis.Z, 5);
        var handoff = Array.Find(
            settings.GetTeachingPositions(new()),
            point => point.Target == TeachingTarget.SupplyBufferHandoff)!;
        await supply.MoveToTeachingPositionAsync(handoff, new() { X = 20, Y = 15, Z = 7 });

        Assert.True(xyMovedTogether);
        Assert.False(movedBelowTransportZ);
        Assert.Equal((20, 15, settings.RotationZ), motion.GetPosition());
        Assert.Equal(TeachMode.XYOnly, handoff.Mode);

        await supply.MoveAxisAsync(MotionAxis.Y, 14);
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => supply.MoveAxisAsync(MotionAxis.Z, 0));
        Assert.Equal((20, 14, settings.RotationZ), motion.GetPosition());

        // Normal XY travel may cross the old buffer boundary at transport height.
        Assert.True(supply.CanMoveToTeachingPosition(handoff));
        await supply.MoveToHandoffAsync(default, new() { X = 0, Y = 0 });
        Assert.True(yMovedInsideBuffer);
        Assert.False(movedBelowTransportZ);
        Assert.Equal((0, 0, settings.RotationZ), motion.GetPosition());
    }

    private static VirtualIoService CreateIo()
    {
        return new(new PcbSupplyHardwareSettings().Outputs, new MachineOptions());
    }
}
