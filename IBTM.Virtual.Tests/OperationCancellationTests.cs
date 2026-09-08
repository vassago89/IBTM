using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class OperationCancellationTests
{
    [Fact]
    public async Task FailedActivityNotificationDoesNotRetainAnOperation()
    {
        var operations = new OperationCancellation();
        var failure = new InvalidOperationException("Activity notification failed.");
        void FailOnce()
        {
            operations.ActivityChanged -= FailOnce;
            throw failure;
        }
        operations.ActivityChanged += FailOnce;

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => operations.Link()));
        Assert.False(operations.HasActiveOperations);
        using (operations.Link()) Assert.True(operations.HasActiveOperations);
        await operations.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RepeatedStopCanRaceWithOperationCompletion()
    {
        var operations = new OperationCancellation();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var operation = operations.Link();
            await Task.WhenAll(
                Task.Run(operation.Cancel),
                Task.Run(operation.Dispose));
            operation.Cancel();
            operation.Dispose();
        }

        var last = operations.Link();
        var shutdown = Task.CompletedTask;
        using var registration = last.Token.Register(() =>
        {
            last.Dispose();
            shutdown = operations.ShutdownAsync();
            Assert.False(shutdown.IsCompleted);
        });
        last.Cancel();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ShutdownCancelsImmediatelyAndWaitsForEveryScope()
    {
        var operations = new OperationCancellation();
        Assert.Throws<OperationCanceledException>(() =>
            operations.Link(new CancellationToken(canceled: true)));
        using var outer = operations.Link();
        using var inner = operations.Link(outer.Token);
        Assert.True(operations.HasActiveOperations);

        var shutdown = operations.ShutdownAsync();

        Assert.True(outer.IsCancellationRequested);
        Assert.True(inner.IsCancellationRequested);
        Assert.True(operations.IsShuttingDown);
        Assert.False(shutdown.IsCompleted);
        Assert.Throws<OperationCanceledException>(() => operations.Link());
        Assert.Same(shutdown, operations.ShutdownAsync());

        outer.Dispose();
        Assert.False(shutdown.IsCompleted);
        Assert.True(operations.HasActiveOperations);
        inner.Dispose();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(operations.HasActiveOperations);
    }

    [Fact]
    public async Task StopAllowsRestartButShutdownWaitsForOldCleanupToo()
    {
        var operations = new OperationCancellation();
        using var previous = operations.Link();
        operations.Cancel();
        using var restarted = operations.Link();

        Assert.True(previous.IsCancellationRequested);
        Assert.False(restarted.IsCancellationRequested);

        var shutdown = operations.ShutdownAsync();
        restarted.Dispose();
        Assert.False(shutdown.IsCompleted);
        previous.Dispose();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        operations.Cancel();
        Assert.Throws<OperationCanceledException>(() => operations.Link());
    }

    [Fact]
    public async Task StopFailureIsReportedOnlyAfterCleanupFinishes()
    {
        var operations = new OperationCancellation();
        using var operation = operations.Link();
        using var registration = operation.Token.Register(
            () => throw new InvalidOperationException("Stop failed"));

        var shutdown = operations.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        operation.Dispose();

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => shutdown.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains(error.Flatten().InnerExceptions,
            exception => exception.Message == "Stop failed");
        Assert.Same(shutdown, operations.ShutdownAsync());
    }

}
