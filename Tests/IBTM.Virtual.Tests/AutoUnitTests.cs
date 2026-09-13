using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class AutoUnitTests
{
    [Fact]
    public async Task StepTraceKeepsWaitReasonAndTargetWithoutRepeatingUnchangedFeedback()
    {
        var unit = new TestUnit();
        var messages = new List<string>();
        unit.Trace += messages.Add;
        using var stop = new CancellationTokenSource();
        var rounds = 0;
        Task Execute(CancellationToken token)
        {
            unit.ReportStep();
            if (++rounds < 4)
                unit.NotifyChanged();
            return unit.WaitAsync(token);
        }

        var run = unit.RunAsync(Execute, stop.Token);
        try
        {
            Assert.Equal(4, rounds);
            Assert.Single(messages, text => text.StartsWith("Waiting for"));
            Assert.Single(messages, text => text.StartsWith("TestUnit: Friday"));
            Assert.Contains(messages, text => text.Contains("target=PCB 2") && text.Contains("work=17"));
            Assert.Contains(messages, text => text.Contains("waitFor=CarrierPresent=ON"));
        }
        finally
        {
            stop.Cancel();
            await run;
        }
        Assert.Contains("cancelled=True", messages.Last());
    }

    [Fact]
    public async Task ChangesCoalesceWithoutOverlappingActions()
    {
        var unit = new TestUnit();
        using var stop = new CancellationTokenSource();
        var finishAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;

        async Task ExecuteAsync(CancellationToken token)
        {
            switch (++executions)
            {
                case 1:
                    await finishAction.Task.WaitAsync(token);
                    break;
                case 2:
                    await unit.WaitAsync(token);
                    break;
                default:
                    waiting.TrySetResult();
                    await unit.WaitAsync(token);
                    break;
            }
        }

        var run = unit.RunAsync(ExecuteAsync, stop.Token);
        for (var i = 0; i < 20; i++)
            unit.NotifyChanged();
        Assert.Equal(1, executions);
        finishAction.SetResult();

        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, executions);
        stop.Cancel();
        await run;
        Assert.False(unit.HasSubscribers);

        using var restarted = new CancellationTokenSource();
        run = unit.RunAsync(ExecuteAsync, restarted.Token);
        Assert.Equal(4, executions);
        restarted.Cancel();
        await run;
        Assert.False(unit.HasSubscribers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorsAndUnrelatedCancellationPropagate(bool cancellation)
    {
        var unit = new TestUnit();
        Exception error = cancellation ? new OperationCanceledException() : new InvalidOperationException();

        var actual = await Record.ExceptionAsync(
            () => unit.RunAsync(_ => Task.FromException(error), CancellationToken.None));

        Assert.Same(error, actual);
        Assert.False(unit.HasSubscribers);
    }

    [Fact]
    public async Task CancelledRunDoesNotStartAnotherAction()
    {
        var unit = new TestUnit();
        using var stop = new CancellationTokenSource();
        var executions = 0;
        Task ExecuteAsync(CancellationToken token)
        {
            executions++;
            unit.NotifyChanged();
            stop.Cancel();
            return Task.CompletedTask;
        }

        await unit.RunAsync(ExecuteAsync, stop.Token);
        await unit.RunAsync(ExecuteAsync, stop.Token);

        Assert.Equal(1, executions);
        Assert.False(unit.HasSubscribers);
    }

    private sealed class TestUnit : AutoUnit
    {
        public override event Action? Changed;
        public bool HasSubscribers
        {
            get
            {
                return Changed is not null;
            }
        }

        public void NotifyChanged()
        {
            Changed?.Invoke();
        }

        public void ReportStep()
        {
            TraceStep(DayOfWeek.Friday, "PCB 2", 17, "CarrierPresent=ON");
        }

        public Task WaitAsync(CancellationToken token)
        {
            return WaitForChangeAsync(token);
        }

        public Task RunAsync(
            Func<CancellationToken, Task> execute,
            CancellationToken token,
            Func<bool>? completed = null)
        {
            return RunLoopAsync(execute, token, completed);
        }
    }

    [Fact]
    public async Task FiniteOperationCompletesWithoutCancellingItsToken()
    {
        var unit = new TestUnit();
        using var stop = new CancellationTokenSource();
        var count = 0;
        await unit.RunAsync(
            _ =>
            {
                count++;
                return Task.CompletedTask;
            },
            stop.Token,
            () => count == 2);
        Assert.Equal(2, count);
        Assert.False(stop.IsCancellationRequested);
        Assert.False(unit.HasSubscribers);
    }
}
