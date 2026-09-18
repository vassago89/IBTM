using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.Device;
using Xunit;

namespace IBTM.Ajin.Tests;

public sealed partial class AjinControllerTests
{
    [Fact]
    public async Task HorizontalHomeStartsBothAxesBeforeWaitingAndWaitsForBothResults()
    {
        using var controller = new AjinController(new());
        var motion = CreateHorizontalHome(controller);
        var bothStarted = false;
        var yReads = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmHomeSetStart))
            {
                var axis = call.Axis!.Value;
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { HomeResult = 2 };
                if (axis == 10)
                {
                    bothStarted = true;
                    AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { HomeResult = 1 };
                }
            }
            if (call.Operation == nameof(CAXM.AxmHomeGetResult)
                && AjinSdk.Calls.Any(item => item.Operation == nameof(CAXM.AxmHomeSetStart)))
            {
                Assert.True(bothStarted);
                if (call.Axis == 10 && ++yReads == 2)
                    AjinSdk.MotionAxes[10] = AjinSdk.MotionAxes[10] with { HomeResult = 1 };
            }
        };

        Assert.True(await motion.HomeHorizontalAsync(1).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(yReads >= 3);
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmHomeSetStart)).Select(call => call.Axis!.Value));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetResult));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public async Task HorizontalHomeFailureStopsBothAxesAndKeepsTheFailedResult(int failedAxis)
    {
        using var controller = new AjinController(new());
        var motion = CreateHorizontalHome(controller);
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmHomeSetStart))
            {
                var axis = call.Axis!.Value;
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with
                {
                    HomeResult = axis == failedAxis ? 0x12U : 2U,
                };
            }
        };

        var error = await Assert.ThrowsAsync<IOException>(() => motion.HomeHorizontalAsync(1));
        Assert.Contains($"axis={failedAxis}", error.Message);
        Assert.Contains("HOME_ERR_VELOCITY", error.Message);
        Assert.Equal(0x12U, AjinSdk.MotionAxes[failedAxis].HomeResult);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetResult));
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmMoveSStop)).Select(call => call.Axis!.Value));
    }

    [Fact]
    public async Task HorizontalHomePreservesBothAxesFailedResults()
    {
        using var controller = new AjinController(new());
        var motion = CreateHorizontalHome(controller);
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmHomeSetStart))
            {
                var axis = call.Axis!.Value;
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { HomeResult = 0x12 };
            }
        };

        var error = await Assert.ThrowsAsync<MotionException>(() => motion.HomeHorizontalAsync(1));
        var failures = Assert.IsType<AggregateException>(error.InnerException).Flatten().InnerExceptions;
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, failure => failure.Message.Contains("axis=9"));
        Assert.Contains(failures, failure => failure.Message.Contains("axis=10"));
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmMoveSStop)).Select(call => call.Axis!.Value));
    }

    [Fact]
    public async Task SecondHomeStartFailureStopsTheFirstAxisBeforeReturning()
    {
        using var controller = new AjinController(new());
        var motion = CreateHorizontalHome(controller);
        AjinSdk.Results[new(nameof(CAXM.AxmHomeSetStart), Axis: 10)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;

        var error = await Assert.ThrowsAsync<IOException>(() => motion.HomeHorizontalAsync(1));
        Assert.Contains("AxmHomeSetStart (axis=10)", error.Message);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetResult));
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmMoveSStop)).Select(call => call.Axis!.Value));
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Fact]
    public async Task HorizontalHomeCancellationWaitsForBothAxesToStop()
    {
        using var controller = new AjinController(new());
        var motion = CreateHorizontalHome(controller);
        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        var stopChecks = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmHomeSetStart))
            {
                var axis = call.Axis!.Value;
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { HomeResult = 2, InMotion = 1 };
                if (axis == 10)
                {
                    cancelled = true;
                    cancellation.Cancel();
                }
            }
            if (cancelled && call.Operation == nameof(CAXM.AxmStatusReadInMotion) && ++stopChecks == 4)
            {
                foreach (var axis in new[] { 9, 10 })
                    AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { InMotion = 0 };
            }
            if (call.Operation == nameof(CAXM.AxmMoveSStop))
            {
                var axis = call.Axis!.Value;
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with
                {
                    HomeResult = (uint)AXT_MOTION_HOME_RESULT.HOME_ERR_USER_BREAK,
                };
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            motion.HomeHorizontalAsync(1, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(stopChecks >= 6);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetResult));
        foreach (var axis in new[] { 9, 10 })
            Assert.Equal((uint)AXT_MOTION_HOME_RESULT.HOME_ERR_USER_BREAK, AjinSdk.MotionAxes[axis].HomeResult);
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmMoveSStop))
            .Select(call => call.Axis!.Value).Distinct());
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Fact]
    public async Task SingleAxisHorizontalHomeNeverCommandsAMissingYAxis()
    {
        using var controller = new AjinController(new());
        var motion = CreateHorizontalHome(controller, hasY: false);
        Assert.True(await motion.HomeHorizontalAsync(1));
        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetStart));
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Axis == 10);
    }

    private static AjinMotionService CreateHorizontalHome(AjinController controller, bool hasY = true)
    {
        foreach (var axis in hasY ? new[] { 9, 10 } : new[] { 9 })
        {
            AjinSdk.MotionAxes[axis] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
            AjinSdk.HomeMethods[axis] = new(0, 4, 0, 0, 0);
            AjinSdk.Results[new(nameof(CAXM.AxmHomeSetStart), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmHomeSetVel), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: axis)] = 0;
        }
        return new AjinMotionService(
            controller, new() { Number = 9 }, hasY ? new() { Number = 10 } : null,
            null, new(), new(), new(), null);
    }
}
