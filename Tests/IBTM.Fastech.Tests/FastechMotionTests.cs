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
        EziMOTIONPlusELib.Reset();
    }

    [Fact]
    public void ConnectionAndFeedbackDoNotEnableServoOrInventState()
    {
        using var controller = new FastechController(new() { IpAddress = "192.168.0.7" });
        var motion = CreateMotion(controller);
        Assert.Null(motion.ReadDiagnosticState(MotionAxis.Z).State);
        EziMOTIONPlusELib.ActualPosition = 6250;
        motion.Initialize();
        Assert.Equal(1.25, motion.Position.Z);
        Assert.IsAssignableFrom<IAxisMotion>(motion);
        Assert.False(typeof(IXyMotion).IsAssignableFrom(motion.GetType()));
        Assert.Equal(new[] { MotionAxis.Z }, motion.Axes);
        Assert.DoesNotContain(EziMOTIONPlusELib.Calls, call => call.StartsWith("Servo:"));

        EziMOTIONPlusELib.Status = 0x08050003;
        var state = motion.GetAxisState(MotionAxis.Z);
        Assert.True(state.InMotion && state.Alarm && state.Emergency && state.PositiveLimit);
        Assert.False(state.Homed || state.ServoOn || state.InPosition);
        EziMOTIONPlusELib.ReadResult = 6;
        Assert.Null(motion.ReadDiagnosticState(MotionAxis.Z).State);
        Assert.Null(motion.ReadDiagnosticPosition(MotionAxis.Z).Position);
        Assert.Throws<IOException>(() => motion.IsReady);
    }

    [Fact]
    public void FailedConnectionIsReportedAndRepeatedInitializationReusesConnection()
    {
        using var controller = new FastechController(new() { IpAddress = "192.168.0.7" });
        EziMOTIONPlusELib.ConnectResult = false;
        Assert.Throws<IOException>(() => controller.Initialize());
        EziMOTIONPlusELib.ConnectResult = true;
        controller.Initialize();
        controller.Initialize();
        Assert.Equal(2, EziMOTIONPlusELib.Calls.Count(call => call.StartsWith("Connect:")));
        Assert.Equal(2, EziMOTIONPlusELib.Calls.Count(call => call == "Status:0"));
        controller.Dispose();
        Assert.Equal(2, EziMOTIONPlusELib.Calls.Count(call => call.StartsWith("Close:")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("::1")]
    public void InvalidAddressDoesNotCallSdk(string address)
    {
        using var controller = new FastechController(new() { IpAddress = address });
        Assert.Throws<ArgumentException>(() => controller.Initialize());
        Assert.Empty(EziMOTIONPlusELib.Calls);
    }

    [Fact]
    public async Task MoveConvertsMillimetersSpeedAndIndependentRamps()
    {
        using var controller = new FastechController(new() { IpAddress = "192.168.0.7" });
        var motion = CreateMotion(controller);
        motion.Initialize();
        await motion.MoveAxisAsync(MotionAxis.Z, 1.25, 0.4, accelerationSeconds: 0.15, decelerationSeconds: 0.07);
        Assert.Equal(6250, EziMOTIONPlusELib.Target);
        Assert.Equal(2000U, EziMOTIONPlusELib.Speed);
        Assert.Equal(150, EziMOTIONPlusELib.MoveOptions!.wCustomAccelTime);
        Assert.Equal(70, EziMOTIONPlusELib.MoveOptions.wCustomDecelTime);
        Assert.Equal(1.25, motion.Position.Z);
        Assert.Equal(MotionCommand.None, motion.Command);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => motion.MoveAxisAsync(MotionAxis.X, 1, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => motion.HomeAsync(MotionAxis.X, 1));
    }

    [Fact]
    public async Task HardwareEditsWaitForRestartWhileMotionSettingsRemainLive()
    {
        var hardware = new FastechSettings
        {
            IpAddress = "192.168.0.7",
            BackupAxis = new() { PulsesPerMillimeter = 5000 },
        };
        var settings = new MotionSettings();
        using var controller = new FastechController(hardware);
        var motion = new FastechZMotion(controller, hardware.BackupAxis, settings, new(), new());
        motion.Initialize();
        EziMOTIONPlusELib.ActualPosition = 6250;

        hardware.IpAddress = "192.168.0.8";
        hardware.BackupAxis.PulsesPerMillimeter = 10000;
        motion.Initialize();
        await motion.ResetAsync();
        Assert.Equal(1.25, motion.Position.Z);
        Assert.DoesNotContain("Reset:0", EziMOTIONPlusELib.Calls);

        settings.AccelerationSeconds = 0.15;
        settings.DecelerationSeconds = 0.07;
        await motion.MoveAxisAsync(MotionAxis.Z, 2, 0.4);
        Assert.Equal(10000, EziMOTIONPlusELib.Target);
        Assert.Equal(150, EziMOTIONPlusELib.MoveOptions!.wCustomAccelTime);
        Assert.Equal(70, EziMOTIONPlusELib.MoveOptions.wCustomDecelTime);

        controller.Dispose();
        motion.Initialize();
        Assert.Equal(2, EziMOTIONPlusELib.Calls.Count(call => call == "Connect:192.168.0.7:0"));
        controller.Dispose();
        using var restartedController = new FastechController(hardware);
        var restartedMotion = new FastechZMotion(restartedController, hardware.BackupAxis, settings, new(), new());
        restartedMotion.Initialize();
        Assert.Contains("Connect:192.168.0.8:0", EziMOTIONPlusELib.Calls);
        Assert.Equal(1, restartedMotion.Position.Z);
    }

    [Fact]
    public async Task ValuesOutsideSdkRangeAreRejectedBeforeMotionStarts()
    {
        using var controller = new FastechController(new() { IpAddress = "192.168.0.7" });
        var motion = CreateMotion(controller);
        motion.Initialize();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => motion.MoveAxisAsync(MotionAxis.Z, 1, 1, accelerationSeconds: 66));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => motion.MoveAxisAsync(MotionAxis.Z, 500000, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => motion.JogAsync(MotionAxis.Z, 1000000));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => motion.HomeAsync(MotionAxis.Z, 500000));
        Assert.False(EziMOTIONPlusELib.MoveStarted.Task.IsCompleted);
        Assert.Empty(EziMOTIONPlusELib.Parameters);
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Fact]
    public async Task MissingArrivalFeedbackReportsMotionFailureAndReleasesTheCommand()
    {
        using var controller = new FastechController(new() { IpAddress = "192.168.0.7" });
        var motion = CreateMotion(controller);
        motion.Initialize();
        EziMOTIONPlusELib.CompleteMove = false;
        var move = motion.MoveAxisAsync(MotionAxis.Z, 1, 1);
        await EziMOTIONPlusELib.MoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        EziMOTIONPlusELib.Status = EziMOTIONPlusELib.Ready & ~0x00080000U;

        var failure = await Assert.ThrowsAsync<MotionException>(() => move);
        Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.Contains("Stop:0", EziMOTIONPlusELib.Calls);
        Assert.Equal(MotionCommand.None, motion.Command);

        await motion.ResetAsync();
        EziMOTIONPlusELib.CompleteMove = true;
        await motion.MoveAxisAsync(MotionAxis.Z, 1, 1);
        Assert.Equal(1, motion.Position.Z);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWaitsForActualStopFeedback(bool machineStop)
    {
        using var controller = new FastechController(new() { IpAddress = "192.168.0.7" });
        var operations = new OperationCancellation();
        var motion = CreateMotion(controller, operations);
        motion.Initialize();
        EziMOTIONPlusELib.CompleteMove = false;
        EziMOTIONPlusELib.CompleteStop = false;
        using var cancellation = new CancellationTokenSource();
        var move = motion.MoveAxisAsync(MotionAxis.Z, 10, 1, machineStop ? default : cancellation.Token);
        await EziMOTIONPlusELib.MoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (machineStop)
            operations.Cancel();
        else
            cancellation.Cancel();
        await EziMOTIONPlusELib.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(move.IsCompleted);
        Assert.True(motion.IsMoving);
        EziMOTIONPlusELib.Status = EziMOTIONPlusELib.Ready;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(MotionCommand.None, motion.Command);
        Assert.False(operations.HasActiveOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetSkipsHealthyDriveWithoutChangingServo(bool servoOn)
    {
        using var controller = new FastechController(new() { IpAddress = "192.168.0.7" });
        var motion = CreateMotion(controller);
        motion.Initialize();
        var status = servoOn ? EziMOTIONPlusELib.Ready : EziMOTIONPlusELib.Ready & ~0x00100000U;
        EziMOTIONPlusELib.Status = status;
        EziMOTIONPlusELib.Calls.Clear();

        await motion.ResetAsync();

        Assert.Equal(new[] { "Status:0" }, EziMOTIONPlusELib.Calls);
        Assert.Equal(status, EziMOTIONPlusELib.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(EziMOTIONPlusELib.FMP_RESETFAIL)]
    public async Task ResetSendsAlarmResetOnlyForFaultAndReportsRejection(int resetResult)
    {
        using var controller = new FastechController(new() { IpAddress = "192.168.0.7" });
        var motion = CreateMotion(controller);
        motion.Initialize();
        EziMOTIONPlusELib.Status = 0x00000001;
        EziMOTIONPlusELib.ResetResult = resetResult;
        EziMOTIONPlusELib.Calls.Clear();

        if (resetResult == 0)
            await motion.ResetAsync();
        else
        {
            var error = await Assert.ThrowsAsync<IOException>(() => motion.ResetAsync());
            Assert.Contains("FAS_ServoAlarmReset", error.Message);
            Assert.Contains("0x86", error.Message);
        }

        Assert.Equal(new[] { "Status:0", "Reset:0" }, EziMOTIONPlusELib.Calls);
        Assert.Equal(resetResult != 0, motion.GetAxisState(MotionAxis.Z).Alarm);
        Assert.False(motion.GetAxisState(MotionAxis.Z).ServoOn);
    }

    [Fact]
    public async Task ResetReportsStatusReadFailureWithoutSendingCommands()
    {
        using var controller = new FastechController(new() { IpAddress = "192.168.0.7" });
        var motion = CreateMotion(controller);
        motion.Initialize();
        EziMOTIONPlusELib.ReadResult = 6;
        EziMOTIONPlusELib.Calls.Clear();

        var error = await Assert.ThrowsAsync<IOException>(() => motion.ResetAsync());

        Assert.Contains("FAS_GetAxisStatus", error.Message);
        Assert.Equal(new[] { "Status:0" }, EziMOTIONPlusELib.Calls);
    }

    [Fact]
    public async Task HomeUsesNegativeLimitWithoutOriginSensor()
    {
        using var controller = new FastechController(new() { IpAddress = "192.168.0.7" });
        var motion = CreateMotion(controller);
        motion.Initialize();
        EziMOTIONPlusELib.Parameters[17] = 4;
        EziMOTIONPlusELib.Parameters[18] = 0;
        EziMOTIONPlusELib.Status = (EziMOTIONPlusELib.Ready & ~0x02000000U) | 0x00000004;
        Assert.True(await motion.HomeAsync(MotionAxis.Z, 3));
        Assert.Equal(15000, EziMOTIONPlusELib.Parameters[14]);
        Assert.Equal(10000, EziMOTIONPlusELib.Parameters[15]);
        Assert.Equal(1000, EziMOTIONPlusELib.Parameters[16]);
        Assert.Equal(2, EziMOTIONPlusELib.Parameters[17]);
        Assert.Equal(1, EziMOTIONPlusELib.Parameters[18]);
        Assert.True(EziMOTIONPlusELib.Calls.IndexOf("Parameter:0:17:2") < EziMOTIONPlusELib.Calls.IndexOf("Home:0"));
        Assert.True(EziMOTIONPlusELib.Calls.IndexOf("Parameter:0:18:1") < EziMOTIONPlusELib.Calls.IndexOf("Home:0"));
        Assert.True(motion.GetAxisState(MotionAxis.Z).Homed);
        Assert.False(motion.GetAxisState(MotionAxis.Z).HomeSensor);
    }

    [Theory]
    [InlineData(1, EziMOTIONPlusELib.DIR_INC)]
    [InlineData(-1, EziMOTIONPlusELib.DIR_DEC)]
    public async Task JogKeepsPositiveUpAndNegativeDown(double velocity, int direction)
    {
        using var controller = new FastechController(new() { IpAddress = "192.168.0.7" });
        var motion = CreateMotion(controller);
        motion.Initialize();
        using var cancellation = new CancellationTokenSource();
        var jog = motion.JogAsync(MotionAxis.Z, velocity, cancellation.Token);
        await EziMOTIONPlusELib.MoveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jog);
        Assert.Contains($"Jog:0:{direction}", EziMOTIONPlusELib.Calls);
        Assert.Equal(5000U, EziMOTIONPlusELib.Speed);
    }

    private static FastechZMotion CreateMotion(FastechController controller, OperationCancellation? operations = null)
    {
        return new(controller, new() { PulsesPerMillimeter = 5000 },
            new(), new() { MotionPollMilliseconds = 10, TimeoutMilliseconds = 1000 }, operations ?? new());
    }
}
