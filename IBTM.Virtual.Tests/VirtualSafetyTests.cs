using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbSupply;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class VirtualSafetyTests
{
    [Fact]
    public void CarrierPointMovesBetweenStationReferences()
    {
        var carrierPoint = CarrierCoordinates.FromMachine(
            new AxisPos { X = 25, Y = 27 },
            new AxisPos { X = 10, Y = 20 },
            new AxisPos { X = 50, Y = 20 });

        var fasteningPoint = CarrierCoordinates.ToMachine(
            carrierPoint,
            new AxisPos { X = 100, Y = 200 },
            new AxisPos { X = 100, Y = 240 });

        Assert.Equal(93, fasteningPoint.X, 6);
        Assert.Equal(215, fasteningPoint.Y, 6);
    }

    [Fact]
    public async Task MotionRetractsZBeforeXyMovement()
    {
        var settings = new MotionSettings
        {
            SafeZ = -5,
            ZSpeed = 100,
        };
        using var motion = new VirtualMotionService(
            settings,
            new OperationCancellation());
        motion.Initialize();
        await motion.HomeAsync(MotionAxis.Z, 100);
        await motion.HomeAsync(MotionAxis.X, 100);
        await motion.HomeAsync(MotionAxis.Y, 100);
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
    public async Task SupplyHomeRotatesBeforeMovingFromAnEmptyUnrotatedState()
    {
        var io = CreateIo();
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            xRange: (0, 200),
            yRange: (0, 200),
            zRange: (0, 100),
            operationCancellation: operations);
        var supply = new PcbSupplyHandler(
            motion,
            io,
            new PcbSupplySettings());
        var rotatedBeforeMotion = false;

        io.Initialize();
        motion.Initialize();
        await motion.HomeAsync(MotionAxis.Z, 1_000);
        await motion.HomeAsync(MotionAxis.X, 1_000);
        await motion.HomeAsync(MotionAxis.Y, 1_000);
        await motion.MoveToAsync(100, 80, 0);
        motion.MovingChanged += moving =>
        {
            if (moving)
            {
                rotatedBeforeMotion =
                    io.GetInput(InputIo.PcbSupplyRotated);
            }
        };

        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        Assert.False(supply.CanPrepareHome);
        io.SetInput(InputIo.PcbSupplyPcbDetected, false);

        var prepared = await supply.PrepareHomeAsync(1_000);
        var homed = prepared && await supply.CompleteHomeAsync(
            1_000,
            1_000);

        Assert.True(homed);
        Assert.True(rotatedBeforeMotion);
        Assert.Equal(PcbSupplyRotation.Rotated, supply.Rotation);
        Assert.True(motion.GetAxisState(MotionAxis.X).Homed);
        Assert.True(motion.GetAxisState(MotionAxis.Y).Homed);
        Assert.True(motion.GetAxisState(MotionAxis.Z).Homed);
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
        var supplyHandoff = new AxisPos { X = 10, Y = 10, Z = 8 };
        var placementHandoff = new AxisPos { X = 10, Y = 10, Z = 8 };
        var io = CreateIo();
        using var supply = new VirtualMotionService(
            settings,
            xRange: (0, 100),
            yRange: (0, 100),
            zRange: (0, 100),
            operationCancellation: operations);
        using var placement = new VirtualMotionService(
            settings,
            xRange: (0, 100),
            yRange: (0, 100),
            zRange: (0, 100),
            operationCancellation: operations);
        var buffer = new BufferStage(
            CreateBufferSettings(),
            io,
            supply,
            placement,
            supplyHandoff,
            placementHandoff);

        io.Initialize();
        supply.Initialize();
        placement.Initialize();
        foreach (var motion in new[] { supply, placement })
        {
            await motion.HomeAsync(MotionAxis.Z, 1_000);
            await motion.HomeAsync(MotionAxis.X, 1_000);
            await motion.HomeAsync(MotionAxis.Y, 1_000);
        }

        await supply.MoveToAsync(10, 10, 8);
        io.SetInput(InputIo.PcbBufferPcbPresent, true);
        Assert.True(buffer.CanPlacementEnter);

        await placement.MoveToAsync(10, 10, 8);
        Assert.False(buffer.Conflict);

        await supply.MoveToAsync(0, 10, 12);
        Assert.False(buffer.Conflict);
        Assert.True(buffer.CanPlacementExit);
    }

    private static VirtualIoService CreateIo() => new(
        new PcbSupplyHardwareSettings().Outputs,
        new MachineOptions());

    private static PcbBufferSettings CreateBufferSettings() => new()
    {
        SupplyBoundary1 = 5,
        SupplyBoundary2 = 50,
        PlacementBoundary1 = new AxisPos { X = 5, Y = 5 },
        PlacementBoundary2 = new AxisPos { X = 30, Y = 12 },
    };
}
