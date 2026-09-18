using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class OperationCancellationTests
{
    [Fact]
    public void TopLevelAdmissionWaitsForCancelledOwnerAndChildCleanup()
    {
        var operations = new OperationCancellation();
        using var owner = operations.TryBegin();
        Assert.NotNull(owner);
        using var child = operations.Link(owner.Token);
        Assert.Null(operations.TryBegin());

        operations.Cancel();
        Assert.True(child.IsCancellationRequested);
        Assert.Null(operations.TryBegin());
        owner.Dispose();
        Assert.Null(operations.TryBegin());
        child.Dispose();

        using var next = operations.TryBegin();
        Assert.NotNull(next);
        Assert.False(next.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedActivityNotificationDoesNotRetainAnOperation(bool failIdleNotification)
    {
        var operations = new OperationCancellation();
        var failure = new InvalidOperationException("Activity notification failed.");
        var idleFailure = new InvalidOperationException("Idle notification failed.");
        void FailNotification()
        {
            if (operations.HasActiveOperations)
                throw failure;
            if (failIdleNotification)
                throw idleFailure;
        }

        operations.ActivityChanged += FailNotification;

        if (failIdleNotification)
        {
            var actual = Assert.Throws<AggregateException>(() => operations.Link());
            Assert.Equal(new[] { failure, idleFailure }, actual.Flatten().InnerExceptions);
        }
        else
        {
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => operations.Link()));
        }

        operations.ActivityChanged -= FailNotification;
        Assert.False(operations.HasActiveOperations);
        using (operations.Link())
            Assert.True(operations.HasActiveOperations);
        await operations.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CancellationAndIdleNotificationFailuresAreBothPreserved()
    {
        var operations = new OperationCancellation();
        using var operation = operations.Link();
        var stopFailure = new InvalidOperationException("Device stop failed.");
        var idleFailure = new InvalidOperationException("Idle notification failed.");
        void FailIdleNotification()
        {
            if (!operations.HasActiveOperations)
                throw idleFailure;
        }

        operations.ActivityChanged += FailIdleNotification;
        using var registration = operation.Token.Register(() =>
        {
            operation.Dispose();
            throw stopFailure;
        });

        var actual = Assert.Throws<AggregateException>(operation.Cancel);
        Assert.Collection(
            actual.InnerExceptions,
            failure => Assert.Same(stopFailure, Assert.Single(Assert.IsType<AggregateException>(failure).InnerExceptions)),
            failure => Assert.Same(idleFailure, failure));
        Assert.False(operations.HasActiveOperations);
        operations.ActivityChanged -= FailIdleNotification;
        await operations.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RepeatedStopCanRaceWithOperationCompletion()
    {
        var operations = new OperationCancellation();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var operation = operations.Link();
            await Task.WhenAll(Task.Run(operation.Cancel), Task.Run(operation.Dispose));
            operation.Cancel();
            operation.Dispose();
        }

        var last = operations.Link();
        var shutdown = Task.CompletedTask;
        using var registration = last.Token.Register(
            () =>
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
        Assert.Throws<OperationCanceledException>(
            () => operations.Link(new CancellationToken(canceled: true)));
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
        Assert.Contains(error.Flatten().InnerExceptions, exception => exception.Message == "Stop failed");
        Assert.Same(shutdown, operations.ShutdownAsync());
    }
}
