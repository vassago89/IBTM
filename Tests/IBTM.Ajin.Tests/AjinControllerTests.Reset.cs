using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.Device;
using Xunit;

namespace IBTM.Ajin.Tests;

public sealed partial class AjinControllerTests
{
    [Fact]
    public async Task AlarmResetReleasesOutputsWithoutServoEnableAndPreservesPositionAndHomeFeedback()
    {
        using var controller = new AjinController(new());
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 4, HomeResult: 1, Position: 12340);
        AjinSdk.MotionAxes[10] = new(Mechanical: 1U << 4, HomeResult: 0, Position: -5670);
        var motion = new AjinMotionService(controller, new() { Number = 9 }, new() { Number = 10 },
            null, new(), new(), new());


        await motion.ResetAsync();
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmSignalServoOn));

        foreach (var axis in new[] { 9, 10 })
        {
            Assert.Contains(new AjinSdk.Call(nameof(CAXM.AxmSignalServoAlarmReset), Value: 1, Axis: axis), AjinSdk.Calls);
            Assert.Contains(new AjinSdk.Call(nameof(CAXM.AxmSignalServoAlarmReset), Value: 0, Axis: axis), AjinSdk.Calls);
            Assert.Equal(0U, AjinSdk.MotionAxes[axis].AlarmReset);
        }
        Assert.Equal((12.34, -5.67, 0), motion.GetPosition());
        Assert.True(motion.GetAxisState(MotionAxis.X).Homed);
        Assert.False(motion.GetAxisState(MotionAxis.Y).Homed);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetResult));
    }

    [Fact]
    public async Task CancelledAlarmResetReleasesOutputsWithoutTurningServosOn()
    {
        using var controller = new AjinController(new());
        AjinSdk.MotionAxes[9] = new();
        AjinSdk.MotionAxes[10] = new();
        var motion = new AjinMotionService(controller, new() { Number = 9 }, new() { Number = 10 },
            null, new(), new(), new());
        using var stop = new CancellationTokenSource();
        AjinSdk.BeforeCall = call =>
        {
            if (call is { Operation: nameof(CAXM.AxmSignalServoAlarmReset), Axis: 10, Value: 1 })
                stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => motion.ResetAsync(stop.Token));

        Assert.All(AjinSdk.MotionAxes.Values, axis => Assert.Equal(0U, axis.AlarmReset));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmSignalServoOn));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlarmResetReportsFailuresAndAttemptsToReleaseEveryAxis(bool failOnCommand)
    {
        using var controller = new AjinController(new());
        AjinSdk.MotionAxes[9] = new();
        AjinSdk.MotionAxes[10] = new();
        var motion = new AjinMotionService(controller, new() { Number = 9 }, new() { Number = 10 },
            null, new(), new(), new());
        if (failOnCommand)
            AjinSdk.Results[new(nameof(CAXM.AxmSignalServoAlarmReset), Value: 1, Axis: 9)] = 1;
        AjinSdk.Results[new(nameof(CAXM.AxmSignalServoAlarmReset), Value: 0, Axis: 9)] = 1;

        var error = await Assert.ThrowsAsync<AggregateException>(() => motion.ResetAsync());

        Assert.Equal(failOnCommand ? 2 : 1, error.InnerExceptions.Count);
        Assert.Contains("axis=9, on=0", error.ToString());
        if (failOnCommand)
            Assert.Contains("axis=9, on=1", error.ToString());
        Assert.Contains(new AjinSdk.Call(nameof(CAXM.AxmSignalServoAlarmReset), Value: 0, Axis: 10), AjinSdk.Calls);
        Assert.Equal(0U, AjinSdk.MotionAxes[10].AlarmReset);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmSignalServoOn));
    }
}
