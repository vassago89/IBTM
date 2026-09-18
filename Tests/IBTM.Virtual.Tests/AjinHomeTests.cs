using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.Device;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class AjinHomeTests
{
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

}
