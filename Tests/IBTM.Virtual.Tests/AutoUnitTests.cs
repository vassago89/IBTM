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
    public async Task StoppedPhaseIsInactiveAndOnlyResumesWhenExplicitlyRequested()
    {
        var unit = new TestUnit();
        using var stop = new CancellationTokenSource();
        await unit.RunAsync(token =>
        {
            unit.ReportStep();
            stop.Cancel();
            return Task.CompletedTask;
        }, stop.Token);

        Assert.Null(unit.Step);
        Assert.Equal(DayOfWeek.Friday, unit.LastPhase);
        using var resume = new CancellationTokenSource();
        await unit.RunAsync(token =>
        {
            Assert.Equal(DayOfWeek.Friday, unit.Step);
            resume.Cancel();
            return Task.CompletedTask;
        }, resume.Token, resumePhase: true);
        Assert.Null(unit.Step);

        using var restart = new CancellationTokenSource();
        await unit.RunAsync(token =>
        {
            Assert.Null(unit.Step);
            restart.Cancel();
            return Task.CompletedTask;
        }, restart.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveStepSurvivesFeedbackChangesAndClearsOnExitWithoutTrace(bool fail)
    {
        var unit = new TestUnit();
        var reported = new List<Enum?>();
        unit.StepChanged += () => reported.Add(unit.Step);
        using var stop = new CancellationTokenSource();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ExecuteAsync(CancellationToken token)
        {
            Assert.Null(unit.Step);
            unit.ReportStep();
            await finish.Task.WaitAsync(token);
        }

        var run = unit.RunAsync(ExecuteAsync, stop.Token);
        Assert.True(unit.IsRunning);
        Assert.Equal(DayOfWeek.Friday, unit.Step);
        unit.NotifyChanged();
        unit.ReportStep();
        Assert.Single(reported);
        Assert.Equal(DayOfWeek.Friday, unit.Step);

        if (fail)
        {
            var error = new InvalidOperationException("device failure");
            finish.SetException(error);
            Assert.Same(error, await Record.ExceptionAsync(() => run));
        }
        else
        {
            stop.Cancel();
            await run;
        }
        Assert.False(unit.IsRunning);
        Assert.Null(unit.Step);
        Assert.Equal(2, reported.Count);
        Assert.Null(reported.Last());
        Assert.False(unit.HasSubscribers);

        using var restart = new CancellationTokenSource();
        await unit.RunAsync(token =>
        {
            Assert.Null(unit.Step);
            unit.ReportStep();
            restart.Cancel();
            return Task.CompletedTask;
        }, restart.Token);
        Assert.Null(unit.Step);
    }

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

    private sealed class TestUnit : AutoUnit
    {
        public override event Action? Changed;

        public bool HasSubscribers => Changed is not null;
        public Enum? LastPhase => SequenceStep;

        public void NotifyChanged()
        {
            Changed?.Invoke();
        }

        public void ReportStep()
        {
            EnterStep(DayOfWeek.Friday, "PCB 2", 17, "CarrierPresent=ON");
        }

        public Task WaitAsync(CancellationToken token)
        {
            return WaitForChangeAsync(token);
        }

        public async Task RunAsync(
            Func<CancellationToken, Task> execute,
            CancellationToken token,
            Func<bool>? completed = null,
            bool resumePhase = false)
        {
            BeginRun(resumePhase ? SequenceStep : null);
            try
            {
                while (!token.IsCancellationRequested && !(completed?.Invoke() ?? false))
                    await execute(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            finally
            {
                EndRun(token);
            }
        }
    }
}
