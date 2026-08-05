using System;
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
    public void SupplyRotationFollowsPositionInputs()
    {
        var io = new VirtualIoService();
        using var motion = new VirtualMotionService(new MotionSettings());
        using var supply = new PcbSupplyHandler(
            motion,
            io,
            new PcbSupplySettings());

        io.Initialize();
        supply.Initialize();
        Assert.Equal(PcbSupplyRotation.Unrotated, supply.Rotation);

        io.SetInput(InputIo.PcbSupplyRotated, true);
        Assert.Equal(PcbSupplyRotation.Between, supply.Rotation);

        io.SetInput(InputIo.PcbSupplyUnrotated, false);
        Assert.Equal(PcbSupplyRotation.Rotated, supply.Rotation);

        io.SetInput(InputIo.PcbSupplyRotated, false);
        Assert.Equal(PcbSupplyRotation.Between, supply.Rotation);

        io.SetInput(InputIo.PcbSupplyUnrotated, true);
        Assert.Equal(PcbSupplyRotation.Unrotated, supply.Rotation);
    }

    [Fact]
    public async Task MotionRetractsZBeforeXyMovement()
    {
        var settings = new MotionSettings
        {
            SafeZ = -5,
            ZSpeed = 100,
        };
        using var motion = new VirtualMotionService(settings);
        motion.Initialize();
        await motion.HomeAsync(MotionAxis.Z, 100);
        await motion.HomeAsync(MotionAxis.X, 100);
        await motion.HomeAsync(MotionAxis.Y, 100);
        await motion.MoveToZAsync(8, 100);

        await motion.MoveToXYAsync(10, 20, 100);

        Assert.Equal((10, 20, -5), motion.GetPosition());
    }

    [Fact]
    public async Task PositiveLimitSearchDoesNotHomeZ()
    {
        using var motion = new VirtualMotionService(
            new MotionSettings(),
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
        var io = new VirtualIoService();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            hasY: false,
            xRange: (0, 200),
            zRange: (0, 100));
        using var supply = new PcbSupplyHandler(
            motion,
            io,
            new PcbSupplySettings());
        var rotatedBeforeMotion = false;

        io.Initialize();
        motion.Initialize();
        supply.Initialize();
        motion.MovingChanged += moving =>
        {
            if (moving)
            {
                rotatedBeforeMotion =
                    io.GetInput(InputIo.PcbSupplyRotated);
            }
        };

        Assert.False(supply.CanHome(bufferPcbPresent: true));
        io.SetInput(InputIo.PcbSupplyPcbPresent, true);
        Assert.False(supply.CanHome(bufferPcbPresent: false));
        io.SetInput(InputIo.PcbSupplyPcbPresent, false);

        var homed = await supply.HomeAsync(
            bufferPcbPresent: false,
            horizontalVelocity: 1_000,
            zVelocity: 1_000);

        Assert.True(homed);
        Assert.True(rotatedBeforeMotion);
        Assert.Equal(PcbSupplyRotation.Rotated, supply.Rotation);
        Assert.True(motion.GetAxisState(MotionAxis.X).Homed);
        Assert.True(motion.GetAxisState(MotionAxis.Z).Homed);
        Assert.Equal((0, 0, 0), motion.GetPosition());
    }

    [Fact]
    public async Task OutputFeedbackTimeoutRaisesAlarm()
    {
        var hardware = new HardwareMap();
        var feedback =
            hardware.OutputFeedbacks[OutputIo.PcbPlacementGripperClose];
        feedback.TimeoutMilliseconds = 20;
        IIoService io = new VirtualIoService(hardware);

        await Assert.ThrowsAsync<IoFeedbackTimeoutException>(
            () => io.SetOutputAndWaitAsync(
                OutputIo.PcbPlacementGripperClose,
                true));
    }

    [Fact]
    public async Task CanceledBufferResetsOnlyAfterOwnerIsClear()
    {
        using var buffer = new BufferStage(new PcbBufferSettings());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => buffer.EnterAsync(BufferOwner.Supply, 10, 0, 0));
        await buffer.EnterAsync(BufferOwner.Supply, 0, 0, 0);
        buffer.Cancel();

        Assert.Equal(BufferOwner.Supply, buffer.Owner);
        Assert.True(buffer.CancellationRequested);

        buffer.ExitSupply(0);

        Assert.Equal(BufferOwner.None, buffer.Owner);
        Assert.False(buffer.CancellationRequested);

        var nextToken = await buffer.EnterAsync(
            BufferOwner.Placement,
            0,
            0,
            0);

        Assert.False(nextToken.IsCancellationRequested);
        buffer.ExitPlacement(0, 0);
    }

}
