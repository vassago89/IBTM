using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.Device;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class AjinHomeTests
{
    [Fact]
    public void AxisSdkUnitAndPulseAreNotAppliedTwiceToCommandsOrFeedback()
    {
        var x = new AxisHardware { Number = 5 };
        var y = new AxisHardware { Number = 4, MoveUnit = 0.1 };
        var z = new AxisHardware { Number = 3, MovePulse = 10 };
        var motion = new AjinMotionService(
            new AjinController(new AjinSettings()),
            x, y, z,
            new MotionSettings(), new MachineOptions(), new OperationCancellation(), null);
        // Exercise conversion without loading the native driver or issuing hardware commands.
        var toUnits = typeof(AjinMotionService).GetMethod("ToUnits", BindingFlags.Static | BindingFlags.NonPublic)!;
        var fromUnits = typeof(AjinMotionService).GetMethod("FromUnits", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var axis in new[] { x, y, z })
        {
            var command = (double)toUnits.Invoke(motion, [1d])!;
            Assert.Equal(1_000d, command);
            Assert.Equal(1d, (double)fromUnits.Invoke(motion, [axis.Number, command, axis.MoveUnit, axis.MovePulse])!, 6);

            // Monitoring remains correct even before our SDK scale is applied.
            var rawPulses = command * axis.MovePulse / axis.MoveUnit;
            Assert.Equal(1d, (double)fromUnits.Invoke(motion, [axis.Number, rawPulses, 1d, 1])!, 6);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => y.MoveUnit = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => y.MovePulse = 0);
        // Edits take effect after restart, not halfway through a move.
        y.MoveUnit = 10;
        Assert.Equal(1d, (double)fromUnits.Invoke(motion, [y.Number, 10_000d, 1d, 1])!, 6);
    }

    [Fact]
    public async Task MoveWaitsForSettledInPositionAndRemainsCancellable()
    {
        var motion = new TestCompletion(new());
        using var cancellation = new CancellationTokenSource();
        var moving = motion.Wait(cancellation.Token);
        Assert.False(moving.IsCompleted);
        motion.Feedback = (false, false, false);
        await Task.Delay(30);
        Assert.False(moving.IsCompleted);
        motion.Feedback = (false, true, false);
        await moving.WaitAsync(TimeSpan.FromSeconds(1));

        motion.Feedback = (false, false, false);
        moving = motion.Wait(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
    }

    [Fact]
    public async Task InPositionTimeoutAndAxisFaultAreNotSuccessfulCompletion()
    {
        var motion = new TestCompletion(new() { TimeoutMilliseconds = 25 });
        motion.Feedback = (false, false, false);
        await Assert.ThrowsAsync<TimeoutException>(() => motion.Wait(CancellationToken.None));
        motion.Feedback = (false, true, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => motion.Wait(CancellationToken.None));
    }

    [Fact]
    public async Task StopWaitsForHardwareMotionOffWithoutRequiringInPosition()
    {
        var motion = new TestCompletion(new());
        var stopping = motion.WaitForStop();
        Assert.False(stopping.IsCompleted);
        motion.Feedback = (false, false, true);
        await stopping.WaitAsync(TimeSpan.FromSeconds(1));

        motion = new TestCompletion(new() { TimeoutMilliseconds = 25 });
        await Assert.ThrowsAsync<TimeoutException>(motion.WaitForStop);
    }

    [Theory]
    [InlineData(MotionAxis.X)]
    [InlineData(MotionAxis.Y)]
    public async Task FailedAxisCancelsItsPartner(MotionAxis failedAxis)
    {
        var motion = new TestAjinHome();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var homing = motion.HomeHorizontal(cancellation.Token);
        motion.Results[(int)failedAxis].SetResult(false);

        Assert.False(await homing);
        var partner = failedAxis == MotionAxis.X ? MotionAxis.Y : MotionAxis.X;
        Assert.True(motion.Tokens[(int)partner].IsCancellationRequested);
    }

    [Fact]
    public async Task BothAxesMustCompleteSuccessfully()
    {
        var motion = new TestAjinHome();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var homing = motion.HomeHorizontal(cancellation.Token);
        motion.Results[(int)MotionAxis.X].SetResult(true);
        Assert.False(homing.IsCompleted);
        motion.Results[(int)MotionAxis.Y].SetResult(true);

        Assert.True(await homing);
        Assert.False(motion.Tokens[0].IsCancellationRequested);
        Assert.False(motion.Tokens[1].IsCancellationRequested);
    }

    [Fact]
    public async Task ExternalCancellationRemainsCancellation()
    {
        var motion = new TestAjinHome();
        using var cancellation = new CancellationTokenSource();
        var homing = motion.HomeHorizontal(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => homing);
        Assert.True(motion.Tokens[0].IsCancellationRequested);
        Assert.True(motion.Tokens[1].IsCancellationRequested);
    }

    [Fact]
    public async Task AxisFailureCancelsItsPartnerAndKeepsTheException()
    {
        var motion = new TestAjinHome();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var homing = motion.HomeHorizontal(cancellation.Token);
        var failure = new InvalidOperationException("Home command failed.");
        motion.Results[0].SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => homing));
        Assert.True(motion.Tokens[1].IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleXAxisReturnsItsOwnResult(bool succeeded)
    {
        var motion = new TestAjinHome(hasY: false);
        motion.Results[0].SetResult(succeeded);

        Assert.Equal(succeeded, await motion.HomeHorizontal(CancellationToken.None));
        Assert.Equal(0, motion.YHomeCalls);
    }

    private sealed class TestCompletion : AjinMotionService
    {
        public (bool Moving, bool InPosition, bool Faulted) Feedback;

        public TestCompletion(MachineOptions options)
            : base(
                new AjinController(new AjinSettings()),
                new AxisHardware(),
                null,
                null,
                new MotionSettings(),
                options,
                new OperationCancellation(),
                null)
        {
            Feedback = (true, false, false);
        }

        public Task Wait(CancellationToken token)
        {
            return WaitForMoveAsync([0], token);
        }

        public Task WaitForStop()
        {
            return WaitForStopAsync([0]);
        }

        protected override (bool Moving, bool InPosition, bool Faulted) ReadMoveState(int[] axes)
        {
            return Feedback;
        }
    }

    private sealed class TestAjinHome : AjinMotionService
    {
        public TestAjinHome(bool hasY = true)
            : base(
                new AjinController(new AjinSettings()),
                new AxisHardware { Number = 0 },
                hasY ? new AxisHardware { Number = 1 } : null,
                axisZ: null,
                new MotionSettings(),
                new MachineOptions(),
                new OperationCancellation(),
                horizontalZ: null)
        {
            Results = [
                new(TaskCreationOptions.RunContinuationsAsynchronously),
                new(TaskCreationOptions.RunContinuationsAsynchronously),
        ];
            Tokens = new CancellationToken[2];
        }

        public TaskCompletionSource<bool>[] Results { get; }
        public CancellationToken[] Tokens { get; }
        public int YHomeCalls { get; private set; }

        public Task<bool> HomeHorizontal(CancellationToken cancellationToken)
        {
            return HomeHorizontalCoreAsync(100, cancellationToken);
        }

        protected override Task<bool> HomeCoreAsync(
            MotionAxis axis,
            double velocity,
            CancellationToken cancellationToken)
        {
            Tokens[(int)axis] = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (axis == MotionAxis.Y)
            {
                YHomeCalls++;
            }

            return Results[(int)axis].Task.WaitAsync(cancellationToken);
        }
    }
}
