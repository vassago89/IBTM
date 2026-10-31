using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FASTECH;
using IBTM.Device;
using Xunit;

namespace IBTM.Fastech.Tests;

public sealed class FastechMotionTests
{
    public FastechMotionTests()
    {
        EziMOTIONPlusRLib.Reset();
    }

    [Fact]
    public void ConnectionAndFeedbackDoNotEnableServoOrInventState()
    {
        using var controller = new FastechController(new() { PortName = "COM7" });
        var motion = CreateMotion(controller);
        Assert.Null(motion.ReadDiagnosticState(MotionAxis.Z).State);
        motion.Initialize();
        Assert.IsAssignableFrom<IAxisMotion>(motion);
        Assert.False(typeof(IXyMotion).IsAssignableFrom(motion.GetType()));
        Assert.Equal(new[] { MotionAxis.Z }, motion.Axes);
        Assert.DoesNotContain(EziMOTIONPlusRLib.Calls, call => call.StartsWith("Servo:"));

        EziMOTIONPlusRLib.Status = 0x08050003;
        var state = motion.GetAxisState(MotionAxis.Z);
        Assert.True(state.InMotion && state.Alarm && state.Emergency && state.PositiveLimit);
        Assert.False(state.Homed || state.ServoOn || state.InPosition);
        EziMOTIONPlusRLib.ReadResult = 6;
        Assert.Null(motion.ReadDiagnosticState(MotionAxis.Z).State);
        Assert.Null(motion.ReadDiagnosticPosition(MotionAxis.Z).Position);
        Assert.Throws<IOException>(() => motion.IsReady);
    }

    [Fact]
    public void FailedConnectionIsReportedAndSharedPortOpensOnlyOnce()
    {
        using var controller = new FastechController(new() { PortName = "COM7" });
        EziMOTIONPlusRLib.ConnectResult = 0;
        Assert.Throws<IOException>(() => controller.Initialize(0));
        EziMOTIONPlusRLib.ConnectResult = 1;
        controller.Initialize(0);
        controller.Initialize(1);
        Assert.Equal(2, EziMOTIONPlusRLib.Calls.Count(call => call.StartsWith("Connect:")));
        Assert.Contains("Status:1", EziMOTIONPlusRLib.Calls);
        controller.Dispose();
        Assert.Equal(2, EziMOTIONPlusRLib.Calls.Count(call => call.StartsWith("Close:")));
    }

    [Fact]
    public async Task MoveConvertsMillimetersSpeedAndIndependentRamps()
    {
        using var controller = new FastechController(new() { PortName = "COM7" });
        var motion = CreateMotion(controller);
        motion.Initialize();
        await motion.MoveAxisAsync(MotionAxis.Z, 1.25, 0.4, accelerationSeconds: 0.15, decelerationSeconds: 0.07);
        Assert.Equal(6250, EziMOTIONPlusRLib.Target);
        Assert.Equal(2000U, EziMOTIONPlusRLib.Speed);
        Assert.Equal(150, EziMOTIONPlusRLib.MoveOptions!.wCustomAccelTime);
        Assert.Equal(70, EziMOTIONPlusRLib.MoveOptions.wCustomDecelTime);
        Assert.Equal(1.25, motion.Position.Z);
        Assert.Equal(MotionCommand.None, motion.Command);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => motion.MoveAxisAsync(MotionAxis.X, 1, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => motion.HomeAsync(MotionAxis.X, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWaitsForActualStopFeedback(bool machineStop)
    {
        using var controller = new FastechController(new() { PortName = "COM7" });
        var operations = new OperationCancellation();
        var motion = CreateMotion(controller, operations);
        motion.Initialize();
        EziMOTIONPlusRLib.CompleteMove = false;
        EziMOTIONPlusRLib.CompleteStop = false;
        using var cancellation = new CancellationTokenSource();
        var move = motion.MoveAxisAsync(MotionAxis.Z, 10, 1, machineStop ? default : cancellation.Token);
        await EziMOTIONPlusRLib.MoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (machineStop)
            operations.Cancel();
        else
            cancellation.Cancel();
        await EziMOTIONPlusRLib.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(move.IsCompleted);
        Assert.True(motion.IsMoving);
        EziMOTIONPlusRLib.Status = EziMOTIONPlusRLib.Ready;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(MotionCommand.None, motion.Command);
        Assert.False(operations.HasActiveOperations);
    }

    [Fact]
    public async Task ResetDoesNotEnableServoAndHomePreservesDriveMethod()
    {
        using var controller = new FastechController(new() { PortName = "COM7" });
        var motion = CreateMotion(controller);
        motion.Initialize();
        EziMOTIONPlusRLib.Parameters[20] = 4;
        EziMOTIONPlusRLib.Parameters[21] = 1;
        await motion.ResetAsync();
        Assert.Contains("Reset:3", EziMOTIONPlusRLib.Calls);
        Assert.DoesNotContain(EziMOTIONPlusRLib.Calls, call => call.StartsWith("Servo:"));
        Assert.True(await motion.HomeAsync(MotionAxis.Z, 2));
        Assert.Equal(10000, EziMOTIONPlusRLib.Parameters[17]);
        Assert.Equal(4, EziMOTIONPlusRLib.Parameters[20]);
        Assert.Equal(1, EziMOTIONPlusRLib.Parameters[21]);
    }

    private static FastechZMotion CreateMotion(FastechController controller, OperationCancellation? operations = null)
    {
        return new(controller, new() { SlaveNumber = 3, PulsesPerMillimeter = 5000 },
            new(), new() { MotionPollMilliseconds = 10, TimeoutMilliseconds = 1000 }, operations ?? new());
    }
}
