using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.Device;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class AjinHomeTests
{
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

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => homing));
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

    private sealed class TestAjinHome(bool hasY = true) : AjinMotionService(
        new AjinController(new AjinSettings()),
        new AxisHardware { Number = 0 },
        hasY ? new AxisHardware { Number = 1 } : null,
        axisZ: null,
        millimetersPerPulse: 0.01,
        new MotionSettings(),
        new OperationCancellation(),
        horizontalZ: null)
    {
        public TaskCompletionSource<bool>[] Results { get; } =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
        ];
        public CancellationToken[] Tokens { get; } = new CancellationToken[2];
        public int YHomeCalls { get; private set; }

        public Task<bool> HomeHorizontal(CancellationToken cancellationToken) =>
            HomeHorizontalCoreAsync(100, cancellationToken);

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
