using System;
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
    public async Task AxisMoveAndJogUseConfiguredAccelerationWithAnyWaveUnits()
    {
        using var controller = new AjinController(new());
        var settings = new MotionSettings { AccelerationSeconds = 0.2, DecelerationSeconds = 0.75 };
        var motion = CreateHorizontalHome(controller, hasY: false, settings: settings);
        using var cancellation = new CancellationTokenSource();
        AjinSdk.Results[new(nameof(CAXM.AxmMovePos), Axis: 9)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveVel), Axis: 9)] = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmMovePos))
                AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { Position = 2500 };
            if (call.Operation == nameof(CAXM.AxmMoveVel))
                cancellation.Cancel();
        };

        await motion.MoveAxisAsync(MotionAxis.X, 2.5, 3);
        settings.AccelerationSeconds = 0.6;
        settings.DecelerationSeconds = 0.3;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            motion.JogAsync(MotionAxis.X, -3, cancellation.Token));

        Assert.Equal(2, AjinSdk.Moves.Count);
        var move = AjinSdk.Moves[0];
        Assert.Equal(new double[] { 2500 }, move.Positions);
        Assert.Equal(new double[] { 3000 }, move.Velocities);
        Assert.Equal(new double[] { 15000 }, move.Accelerations);
        Assert.Equal(new double[] { 4000 }, move.Decelerations);
        var jog = AjinSdk.Moves[1];
        Assert.Null(jog.Positions);
        Assert.Equal(new double[] { -3000 }, jog.Velocities);
        Assert.Equal(new double[] { 5000 }, jog.Accelerations);
        Assert.Equal(new double[] { 10000 }, jog.Decelerations);
        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        Assert.Equal(MotionCommand.None, motion.Command);
    }

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
        Assert.Equal(new[] { 9, 10 }, AjinSdk.Calls
            .Where(call => call.Operation == nameof(CAXM.AxmHomeSetResult)).Select(call => call.Axis!.Value));
    }

    [Fact]
    public async Task InvalidAccelerationNeverStartsMotionOrHome()
    {
        using var controller = new AjinController(new());
        var settings = new MotionSettings { AccelerationSeconds = 0 };
        var motion = CreateHorizontalHome(controller, settings: settings);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => motion.MoveAxisAsync(MotionAxis.X, 2, 1));
        settings.HorizontalHome.SearchAccelerationSeconds = 0;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => motion.HomeHorizontalAsync(1));

        Assert.Empty(AjinSdk.Moves);
        Assert.DoesNotContain(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmHomeSetStart));
        Assert.Equal(MotionCommand.None, motion.Command);
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

        Assert.False(await motion.HomeHorizontalAsync(1));
        Assert.Equal(0x12U, AjinSdk.MotionAxes[failedAxis].HomeResult);
        Assert.Equal(2, AjinSdk.Calls.Count(call => call.Operation == nameof(CAXM.AxmHomeSetResult)));
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

        Assert.False(await motion.HomeHorizontalAsync(1));
        Assert.Equal(2, AjinSdk.Calls.Count(call => call.Operation == nameof(CAXM.AxmHomeSetResult)));
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
        Assert.Equal(2, AjinSdk.Calls.Count(call => call.Operation == nameof(CAXM.AxmHomeSetResult)));
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

    private static AjinMotionService CreateHorizontalHome(
        AjinController controller,
        bool hasY = true,
        MotionSettings? settings = null)
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
            null, settings ?? new(), new(), new(), null);
    }
}
