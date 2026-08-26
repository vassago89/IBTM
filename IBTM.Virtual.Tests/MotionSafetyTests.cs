using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Virtual;
using Xunit;

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

        await motion.MoveToXYAsync(10, 20, 100);

        Assert.Equal((10, 20, -5), motion.GetPosition());
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
            new PcbSupplySettings());
        var rotatedBeforeMotion = false;

        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion);
        await motion.MoveToAsync(100, 80, 0);
        motion.MovingChanged += moving =>
        {
            if (moving)
            {
                rotatedBeforeMotion = io.GetInput(InputIo.PcbSupplyRotated);
            }
        };

        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        Assert.False(supply.CanPrepareHome);
        io.SetInput(InputIo.PcbSupplyPcbDetected, false);

        var prepared = await supply.PrepareHomeAsync(1_000);
        var homed = prepared
            && await supply.CompleteHomeAsync(1_000, 1_000);

        Assert.True(homed);
        Assert.True(rotatedBeforeMotion);
        Assert.Equal(PcbSupplyRotation.Rotated, supply.Rotation);
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
        var handoff = new AxisPos { X = 10, Y = 10, Z = 8 };
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
                PlacementBoundary1 = new AxisPos { X = 5, Y = 5 },
                PlacementBoundary2 = new AxisPos { X = 30, Y = 12 },
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
        await placement.MoveToAsync(0, 0, 0);

        await supply.MoveToAsync(10, 10, 8);
        io.SetInput(InputIo.PcbBufferPcbPresent, true);
        Assert.True(buffer.CanPlacementEnter);

        await placement.MoveToAsync(10, 10, 8);
        Assert.False(buffer.Conflict);
    }

    private static VirtualIoService CreateIo() => new(
        new PcbSupplyHardwareSettings().Outputs,
        new MachineOptions());

    private static VirtualMotionService Motion(
        MotionSettings settings,
        OperationCancellation operations) => new(
            settings,
            xRange: (0, 100),
            yRange: (0, 100),
            zRange: (0, 100),
            horizontalZ: () => 0,
            operationCancellation: operations);

    private static async Task HomeAsync(VirtualMotionService motion)
    {
        await motion.HomeAsync(MotionAxis.Z, 1_000);
        await motion.HomeAsync(MotionAxis.X, 1_000);
        await motion.HomeAsync(MotionAxis.Y, 1_000);
    }
}
