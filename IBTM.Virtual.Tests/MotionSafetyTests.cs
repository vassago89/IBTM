using System;
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
    [Fact]
    public async Task MotionRetractsZBeforeXyMovement()
    {
        var settings = new MotionSettings { ZSpeed = 100 };
        using var motion = new VirtualMotionService(
            settings,
            new OperationCancellation(),
            horizontalZ: () => -5);
        motion.Initialize();
        await HomeAsync(motion);
        await motion.MoveZAsync(8, 100);
        var movedXyBeforeZClear = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if ((x != 0 || y != 0)
                && System.Math.Abs(z + 5)
                    > MotionService.PositionToleranceMillimeters)
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
            zRange: (0, 100));
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
            yRange: (0, 200),
            zRange: (0, 100),
            horizontalZ: () => 0,
            operationCancellation: new OperationCancellation());
        var supply = new PcbSupplyHandler(
            motion,
            io,
            new PcbSupplySettings(),
            new PcbBufferSettings());
        var rotatedBeforeMotion = false;
        var horizontalMovedBeforeZLimit = false;
        var yMovedBeforeXHome = false;
        var previous = motion.GetPosition();

        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion);
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
            if ((movedX || movedY)
                && !motion.GetAxisState(MotionAxis.Z).PositiveLimit)
            {
                horizontalMovedBeforeZLimit = true;
            }

            if (movedY
                && System.Math.Abs(x)
                    > MotionService.PositionToleranceMillimeters)
            {
                yMovedBeforeXHome = true;
            }

            previous = (x, y, z);
        };

        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        Assert.False(supply.CanPrepareHome);
        io.SetInput(InputIo.PcbSupplyPcbDetected, false);

        var prepared = await supply.PrepareHomeAsync(1_000);
        var homed = prepared
            && await supply.CompleteHomeAsync(1_000, 1_000);

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
        var placementHandler = new PcbPlacementHandler(
            placement,
            io,
            new PcbPlacementHandlerSettings());
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
        await Task.WhenAll(HomeAsync(supply), HomeAsync(placement));

        await placement.MoveToAsync(10, 10, 0);
        Assert.False(buffer.PlacementBlocksSupply);
        Assert.True(buffer.CanSupplyEnter);
        await placement.MoveZAsync(8, settings.ZSpeed);
        Assert.True(buffer.PlacementBlocksSupply);
        Assert.False(buffer.CanSupplyEnter);
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
    public async Task SupplyMovesAboveBufferBeforeRotatingAndLowersRotated()
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
            new PcbBufferSettings
            {
                SupplyBoundary1 = 10,
                SupplyBoundary2 = 30,
            });

        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion);

        var xMovedBeforeY = false;
        motion.PositionChanged += (x, y, _) =>
        {
            if (x > MotionService.PositionToleranceMillimeters
                && System.Math.Abs(y - 15)
                    > MotionService.PositionToleranceMillimeters)
            {
                xMovedBeforeY = true;
            }
        };

        await supply.MoveHorizontalAsync(20, 15);

        Assert.False(xMovedBeforeY);
        Assert.Equal((20, 15, 0), motion.GetPosition());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            supply.LowerToHandoffAsync(default));

        await supply.SetRotatedAsync(true);
        await supply.LowerToHandoffAsync(default);
        Assert.Equal((20, 15, 5), motion.GetPosition());
    }

    private static VirtualIoService CreateIo() => new(
        new PcbSupplyHardwareSettings().Outputs,
        new MachineOptions());

    private static async Task HomeAsync(VirtualMotionService motion)
    {
        await motion.HomeAsync(MotionAxis.Z, 1_000);
        await motion.HomeAsync(MotionAxis.X, 1_000);
        await motion.HomeAsync(MotionAxis.Y, 1_000);
    }
}
