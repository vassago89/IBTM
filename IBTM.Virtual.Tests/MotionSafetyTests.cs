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
    [InlineData(MotionAxis.X, 2, 0, 0)]
    [InlineData(MotionAxis.Y, 0, 2, 0)]
    [InlineData(MotionAxis.Z, 0, 0, 2)]
    public async Task SingleAxisAdjustmentMovesOnlyTheRequestedAxis(
        MotionAxis axis,
        double x,
        double y,
        double z)
    {
        using var motion = new VirtualMotionService(new MotionSettings(), new OperationCancellation());
        motion.Initialize();

        await motion.AdjustAxisAsync(axis, 2, 1_000);

        Assert.Equal((x, y, z), motion.GetPosition());
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
        display.RefreshControlFeedback();
        Assert.True(display.Axes[MotionAxis.X].State!.Value.Homed);
        Assert.Equal(horizontal, display.Axes[MotionAxis.Y].State!.Value.Homed);
        Assert.Equal(horizontal, display.XyHomed);

        motion.SetServo(MotionAxis.X, false);
        display.RefreshControlFeedback();
        Assert.False(display.Axes[MotionAxis.X].ServoOn);
        Assert.Equal(AxisCondition.ServoOff, display.Axes[MotionAxis.X].Condition);
        Assert.True(display.Axes[MotionAxis.X].State!.Value.Homed);
    }

    [Fact]
    public async Task SlowJogAccumulatesSubPulseDistanceAndStopsOnCancellation()
    {
        const double pulseLength = MotionHardwareSettings.DefaultMillimetersPerPulse;
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
    public async Task MotionRetractsZBeforeXyMovement()
    {
        var settings = new MotionSettings { ZSpeed = 100 };
        using var motion = new VirtualMotionService(
            settings,
            new OperationCancellation(),
            horizontalZ: () => -5);
        motion.Initialize();
        await HomeAsync(motion, 1_000);
        await motion.MoveZAsync(8, 100);
        var movedXyBeforeZClear = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if ((x != 0 || y != 0)
                && System.Math.Abs(z + 5) > MotionService.PositionToleranceMillimeters)
            {
                movedXyBeforeZClear = true;
            }
        };

        await motion.MoveToXYAsync(10, 20, 100);

        Assert.Equal((10, 20, -5), motion.GetPosition());
        Assert.False(movedXyBeforeZClear);
    }

    [Fact]
    public async Task PositiveLimitSearchDoesNotHomeZ()
    {
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            new OperationCancellation(),
            zRange: (
                0,
                100));
        motion.Initialize();

        await motion.MoveZToPositiveLimitAsync(1_000);

        Assert.Equal(100, motion.GetPosition().Z);
        Assert.True(motion.GetAxisState(MotionAxis.Z).PositiveLimit);
        Assert.False(motion.GetAxisState(MotionAxis.Z).Homed);
    }

    [Fact]
    public async Task SupplyHomeRotatesBeforeHorizontalMovement()
    {
        var io = CreateIo();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            xRange: (0, 200),
            yRange: (
                0,
                200),
            zRange: (
                0,
                100),
            horizontalZ: () => 0,
            operationCancellation: new OperationCancellation());
        var supply = new PcbSupplyHandler(
            motion,
            io,
            new PcbSupplySettings { Motion = new() { HorizontalHome = new() { SearchSpeed = 1_000 }, ZHome = new() { SearchSpeed = 1_000 } } },
            new PcbBufferSettings());
        var rotatedBeforeMotion = false;
        var horizontalMovedBeforeZLimit = false;
        var yMovedBeforeXHome = false;
        var previous = motion.GetPosition();

        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 1_000);
        await motion.MoveToAsync(100, 80, 0);
        previous = motion.GetPosition();
        motion.MovingChanged += moving =>
        {
            if (moving)
            {
                rotatedBeforeMotion = io.GetInput(InputIo.PcbSupplyRotated);
            }
        };
        motion.PositionChanged += (x, y, z) =>
        {
            var movedX = System.Math.Abs(x - previous.X) > 0.001;
            var movedY = System.Math.Abs(y - previous.Y) > 0.001;
            if ((movedX || movedY) && !motion.GetAxisState(MotionAxis.Z).PositiveLimit)
            {
                horizontalMovedBeforeZLimit = true;
            }

            if (movedY && System.Math.Abs(x) > MotionService.PositionToleranceMillimeters)
            {
                yMovedBeforeXHome = true;
            }

            previous = (x, y, z);
        };

        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        Assert.False(supply.CanPrepareHome);
        io.SetInput(InputIo.PcbSupplyPcbDetected, false);

        var prepared = await supply.PrepareHomeAsync();
        var homed = prepared && await supply.CompleteHomeAsync();

        Assert.True(homed);
        Assert.True(rotatedBeforeMotion);
        Assert.False(horizontalMovedBeforeZLimit);
        Assert.False(yMovedBeforeXHome);
        Assert.Equal(PcbSupplyRotationState.Rotated, supply.Rotation);
        Assert.Equal((0, 0, 0), motion.GetPosition());
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
            io,
            placementHandler,
            supply,
            placement,
            handoff,
            handoff,
            () => 0);

        io.Initialize();
        supply.Initialize();
        placement.Initialize();
        await Task.WhenAll(HomeAsync(supply, 1_000), HomeAsync(placement, 1_000));

        await placement.MoveToAsync(10, 10, 0);
        Assert.True(buffer.CanSupplyEnter);
        await placement.MoveZAsync(8, settings.ZSpeed);
        Assert.False(buffer.CanSupplyEnter);
        Assert.False(buffer.CanSupplyLower);
        await placement.MoveToAsync(0, 0, 0);

        await supply.MoveToAsync(20, 10, 8);
        io.SetInput(InputIo.PcbBufferPcbPresent, true);
        Assert.False(buffer.CanPlacementEnter);

        await placement.MoveToAsync(15, 10, 8);
        Assert.True(buffer.Conflict);
        await placement.MoveToAsync(0, 0, 0);

        await supply.MoveToAsync(10, 10, 8);
        Assert.True(buffer.CanPlacementEnter);

        await placement.MoveToAsync(10, 10, 8);
        Assert.False(buffer.Conflict);
    }

    [Fact]
    public async Task SupplyRotatesBeforeBufferEntryAndMovesYBeforeX()
    {
        var io = CreateIo();
        var settings = new PcbSupplySettings
        {
            Motion = new MotionSettings
            {
                HorizontalSpeed = 1_000,
                ZSpeed = 1_000,
            },
            RotationZ = 0,
            BufferHandoffPosition = new AxisPosition
            {
                X = 20,
                Y = 15,
                Z = 5,
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

        var xMovedBeforeY = false;
        motion.PositionChanged += (x, y, _) =>
        {
            if (x > MotionService.PositionToleranceMillimeters
                && System.Math.Abs(y - 15) > MotionService.PositionToleranceMillimeters)
            {
                xMovedBeforeY = true;
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => supply.MoveToHandoffZAsync(default));

        await supply.SetRotatedAsync(true);
        var handoff = Array.Find(
            settings.GetTeachingPositions(new()),
            point => point.Target == TeachingTarget.SupplyBufferHandoff)!;
        await supply.MoveToTeachingPositionAsync(handoff, new() { X = 20, Y = 15, Z = 7 });

        Assert.False(xMovedBeforeY);
        Assert.Equal((20, 15, 7), motion.GetPosition());
        Assert.Equal(5, settings.BufferHandoffPosition.Z);
    }

    private static VirtualIoService CreateIo()
    {
        return new(new PcbSupplyHardwareSettings().Outputs, new MachineOptions());
    }

}
