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
    public void ActualPositionUsesSdkUnitsWithoutRescalingFromStoredAxisSettings()
    {
        using var controller = new AjinController(new());
        var axis = new AxisHardware { Number = 9, MoveUnit = 10, MovePulse = 100 };
        var motion = new AjinMotionService(controller, axis, null, null, new(), new(), new(), null);
        AjinSdk.MotionAxes[9] = new(Position: 12340, Unit: 10, Pulse: 100);
        Assert.Equal(12.34, motion.GetPosition().X);

        // Editing configuration must not change a coordinate already scaled by the SDK.
        axis.MoveUnit = 1;
        axis.MovePulse = 1000;
        Assert.Equal(12.34, motion.GetPosition().X);
        Assert.Equal(12.34, motion.ReadDiagnosticPosition(MotionAxis.X).Position);
        AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { Position = -5670, Unit = 0.1, Pulse = 1 };
        Assert.Equal(-5.67, motion.GetPosition().X);
        Assert.All(AjinSdk.Calls, call => Assert.Equal(nameof(CAXM.AxmStatusGetActPos), call.Operation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositionMovesUseAbsoluteTargetsEvenAfterExternalModeChanges(bool xy)
    {
        using var controller = new AjinController(new());
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, new() { Number = 11 },
            new(), new(), new(), () => -5);
        foreach (var axis in new[] { 9, 10, 11 })
        {
            AjinSdk.MotionAxes[axis] = new(
                Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1, Position: -10000, AbsRelMode: 1);
            AjinSdk.Results[new(nameof(CAXM.AxmMovePos), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmMoveStartPos), Axis: axis)] = 0;
            AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: axis)] = 0;
        }
        AjinSdk.Results[new(nameof(CAXM.AxmMoveMultiPos))] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveStartMultiPos))] = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation is not (nameof(CAXM.AxmMovePos) or nameof(CAXM.AxmMoveMultiPos)
                or nameof(CAXM.AxmMoveStartPos) or nameof(CAXM.AxmMoveStartMultiPos)))
                return;
            var move = AjinSdk.Moves.Last();
            for (var index = 0; index < move.Axes.Length; index++)
            {
                var axis = move.Axes[index];
                var state = AjinSdk.MotionAxes[axis];
                AjinSdk.MotionAxes[axis] = state with
                {
                    Position = move.Positions![index]
                        + (call.Operation == nameof(CAXM.AxmMoveMultiPos) || state.AbsRelMode == 0 ? 0 : state.Position),
                };
            }
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            // An external motion utility can change this after initialization or any previous move.
            foreach (var axis in new[] { 9, 10, 11 })
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { AbsRelMode = 1 };
            if (xy)
                await motion.MoveToXYAsync(20 + attempt, 30 + attempt, 10);
            else
                await motion.MoveAxisAsync(MotionAxis.Z, -30 - attempt, 10);
        }

        Assert.Equal(xy ? (21.0, 31.0, -10.0) : (-10.0, -10.0, -31.0), motion.GetPosition());
        foreach (var axis in xy ? new[] { 9, 10 } : new[] { 11 })
            Assert.Equal(0U, AjinSdk.MotionAxes[axis].AbsRelMode);
    }

    [Theory]
    [InlineData(nameof(CAXM.AxmMotSetAbsRelMode))]
    [InlineData(nameof(CAXM.AxmMotGetAbsRelMode))]
    public async Task FailedAbsoluteModeSetupPreventsPositionCommand(string failedOperation)
    {
        using var controller = new AjinController(new());
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, null,
            new(), new(), new(), null);
        foreach (var axis in new[] { 9, 10 })
        {
            AjinSdk.MotionAxes[axis] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1, AbsRelMode: 1);
            AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: axis)] = 0;
        }
        AjinSdk.Results[new(failedOperation, Axis: 10,
            Value: failedOperation == nameof(CAXM.AxmMotSetAbsRelMode) ? 0U : null)] =
            (uint)AXT_FUNC_RESULT.AXT_RT_NOT_OPEN;

        var failure = await Assert.ThrowsAsync<IOException>(() => motion.MoveToXYAsync(20, 30, 10));

        Assert.Contains(failedOperation, failure.Message);
        Assert.Empty(AjinSdk.Moves);
        Assert.Equal((0.0, 0.0, 0.0), motion.GetPosition());
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositionFeedbackFailuresStopAndReportMotionErrors(bool axisFault)
    {
        using var controller = new AjinController(new());
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, null, null,
            new(), new() { TimeoutMilliseconds = 20 }, new(), null);
        AjinSdk.MotionAxes[9] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
        AjinSdk.Results[new(nameof(CAXM.AxmMoveStartPos), Axis: 9)] = 0;
        AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: 9)] = 0;
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == nameof(CAXM.AxmMoveStartPos))
                AjinSdk.MotionAxes[9] = AjinSdk.MotionAxes[9] with { Mechanical = axisFault ? 1U << 4 : 0 };
        };

        var error = await Record.ExceptionAsync(() => motion.MoveAxisAsync(MotionAxis.X, 10, 10));

        if (axisFault)
            Assert.IsType<MotionInterlockException>(error);
        else
            Assert.IsType<TimeoutException>(Assert.IsType<MotionException>(error).InnerException);
        Assert.Single(AjinSdk.Calls, call => call.Operation == nameof(CAXM.AxmMoveSStop));
        Assert.Equal(MotionCommand.None, motion.Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositionCancellationStopsBeforeNativeMoveCompletion(bool xy)
    {
        using var controller = new AjinController(new());
        var operations = new OperationCancellation();
        var motion = new AjinMotionService(
            controller, new() { Number = 9 }, new() { Number = 10 }, new() { Number = 11 },
            new(), new(), operations, () => 0);
        var axes = xy ? new[] { 9, 10 } : new[] { 11 };
        foreach (var axis in new[] { 9, 10, 11 })
        {
            AjinSdk.MotionAxes[axis] = new(Mechanical: 1U << 5, HomeResult: 1, ServoOn: 1);
            AjinSdk.Results[new(nameof(CAXM.AxmMoveSStop), Axis: axis)] = 0;
        }
        var blockingCommand = xy ? nameof(CAXM.AxmMoveMultiPos) : nameof(CAXM.AxmMovePos);
        var startCommand = xy ? nameof(CAXM.AxmMoveStartMultiPos) : nameof(CAXM.AxmMoveStartPos);
        AjinSdk.Results[new(blockingCommand, Axis: xy ? null : 11)] = 0;
        AjinSdk.Results[new(startCommand, Axis: xy ? null : 11)] = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBlockingCommand = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        AjinSdk.BeforeCall = call =>
        {
            if (call.Operation == blockingCommand || call.Operation == startCommand)
            {
                foreach (var axis in axes)
                    AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { InMotion = 1, Position = -1000 };
                started.TrySetResult();
                // The legacy SDK call does not return until motion ends; the start API returns immediately.
                if (call.Operation == blockingCommand)
                    releaseBlockingCommand.Wait(TimeSpan.FromSeconds(5));
            }
            if (call.Operation == nameof(CAXM.AxmMoveSStop))
            {
                var axis = call.Axis!.Value;
                AjinSdk.MotionAxes[axis] = AjinSdk.MotionAxes[axis] with { InMotion = 0 };
                if (axes.All(number => AjinSdk.MotionAxes[number].InMotion == 0))
                    stopped.TrySetResult();
            }
        };

        var moving = xy
            ? motion.MoveToXYAsync(-20, -30, 10, cancellation.Token)
            : motion.MoveAxisAsync(MotionAxis.Z, -30, 10, cancellation.Token);
        var stoppedBeforeCompletion = false;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(1)));
            stoppedBeforeCompletion = stopped.Task.IsCompletedSuccessfully;
        }
        finally
        {
            releaseBlockingCommand.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => moving.WaitAsync(TimeSpan.FromSeconds(2)));
        }

        Assert.True(stoppedBeforeCompletion, "Cancellation must stop the axes before the native move returns.");
        Assert.All(axes, axis => Assert.Equal(-1000, AjinSdk.MotionAxes[axis].Position));
        Assert.Equal(axes, AjinSdk.Calls.Where(call => call.Operation == nameof(CAXM.AxmMoveSStop))
            .Select(call => call.Axis!.Value));
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(MotionCommand.None, motion.Command);
    }
}
