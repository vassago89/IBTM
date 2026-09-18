using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.UI;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class CommandShutdownTests
{
    [Fact]
    public async Task StopRetainsCommandFailureThatCompletesBeforeCancellationWait()
    {
        var failure = new IOException("Command cleanup failed during STOP.");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(() => pending.Task);
        var execution = command.ExecuteAsync(null);
        var running = CommandShutdown.Capture(command);
        pending.SetException(failure);

        var shutdown = CommandShutdown.CancelAndWaitAsync([command], Task.CompletedTask, running);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => execution));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => shutdown));
    }

    [Fact]
    public async Task WaitDrainsEveryTaskAndPreservesEveryFailure()
    {
        var firstFailure = new IOException("First device stop failed.");
        var lastFailure = new IOException("Last device stop failed.");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = CommandShutdown.WaitAsync(
            Task.FromException(firstFailure),
            Task.FromCanceled(new CancellationToken(canceled: true)),
            pending.Task);

        Assert.False(shutdown.IsCompleted);
        pending.SetException(lastFailure);

        var actual = await Assert.ThrowsAsync<AggregateException>(() => shutdown);
        Assert.Equal(new[] { firstFailure, lastFailure }, actual.Flatten().InnerExceptions);
    }

    [Fact]
    public async Task WaitKeepsSingleFailureAndAcceptsCompletedCancellation()
    {
        var failure = new IOException("Device stop failed.");
        var canceled = Task.FromCanceled(new CancellationToken(canceled: true));

        await CommandShutdown.WaitAsync(Task.CompletedTask, canceled);
        Assert.Same(
            failure,
            await Assert.ThrowsAsync<IOException>(() => CommandShutdown.WaitAsync(
                Task.FromException(failure), canceled)));
    }

    [Fact]
    public async Task FailedStopStillDrainsCommandsAndPreservesBothFailures()
    {
        var stopFailure = new IOException("Cancel callback failed.");
        var commandFailure = new IOException("Command cleanup failed.");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(async token =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            await pending.Task;
        });
        var execution = command.ExecuteAsync(null);
        var shutdown = CommandShutdown.CancelAndWaitAsync([command], Task.FromException(stopFailure));

        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(command.IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);
        pending.SetException(commandFailure);

        Assert.Same(commandFailure, await Assert.ThrowsAsync<IOException>(() => execution));
        var actual = await Assert.ThrowsAsync<AggregateException>(() => shutdown);
        Assert.Equal(new[] { stopFailure, commandFailure }, actual.Flatten().InnerExceptions);
    }

    [Fact]
    public async Task StopCancelsRemainingCommandsWhenFirstCancellationFails()
    {
        var cancelFailure = new IOException("First command cancellation failed.");
        var cleanupFailure = new IOException("First command cleanup failed.");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new AsyncRelayCommand(async token =>
        {
            using var registration = token.Register(() => throw cancelFailure);
            await pending.Task;
        });
        var second = new AsyncRelayCommand(token => Task.Delay(Timeout.Infinite, token));
        var firstRun = first.ExecuteAsync(null);
        var secondRun = second.ExecuteAsync(null);
        try
        {
            var shutdown = CommandShutdown.CancelAndWaitAsync([first, second]);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => secondRun.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(first.IsCancellationRequested);
            Assert.True(second.IsCancellationRequested);
            Assert.False(shutdown.IsCompleted);

            pending.SetException(cleanupFailure);
            Assert.Same(cleanupFailure, await Assert.ThrowsAsync<IOException>(() => firstRun));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondRun);
            var failures = (await Assert.ThrowsAsync<AggregateException>(() => shutdown)).Flatten().InnerExceptions;
            Assert.Equal(2, failures.Count);
            Assert.Contains(cancelFailure, failures);
            Assert.Contains(cleanupFailure, failures);
        }
        finally
        {
            pending.TrySetResult();
            second.Cancel();
        }
    }
}
