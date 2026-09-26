using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Core;

public abstract class AutoUnit
{
    private readonly AsyncAutoResetEvent _stateChanged;
    private string? _lastStep;
    private bool _waiting;

    protected AutoUnit()
    {
        _stateChanged = new();
    }

    public event Action? Changed;
    public event Action? StepChanged;
    public event Action<string>? Trace;

    // Current execution/wait only. Units own any unfinished handoff history.
    // Reading this property never selects the next operation or reads hardware.
    public Enum? Step { get; private set; }
    public bool IsRunning { get; private set; }

    protected void NotifyChanged()
    {
        Changed?.Invoke();
    }

    // Peer changes wake this loop without broadcasting back to the peer.
    protected void WakeRun()
    {
        _stateChanged.Set();
    }

    protected void EnterStep(Enum step, string? target = null, long? workId = null, string? waitingFor = null)
    {
        if (!IsRunning)
            return;
        if (!Equals(Step, step))
        {
            Step = step;
            StepChanged?.Invoke();
        }
        TraceStep(step, target, workId, waitingFor);
    }

    // Add the target/wait reason without changing the executing step.
    protected void TraceStep(Enum step, string? target = null, long? workId = null, string? waitingFor = null)
    {
        if (!IsRunning || Trace is null)
            return;
        var detail = $"{GetType().Name}: {step} ({step.GetDescription()})"
            + (target is null ? "" : $"; target={target}")
            + (workId is null ? "" : $"; work={workId}")
            + (waitingFor is null ? "" : $"; waitFor={waitingFor}");
        if (_lastStep == detail)
            return;
        _lastStep = detail;
        _waiting = false;
        Trace.Invoke(detail);
    }

    protected Task WaitForChangeAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (!_waiting && _lastStep is not null)
        {
            _waiting = true;
            Trace?.Invoke($"Waiting for feedback / work change: {_lastStep}");
        }
        return timeout is { } duration
            ? _stateChanged.WaitAsync(duration, cancellationToken)
            : _stateChanged.WaitAsync(cancellationToken);
    }

    protected void BeginRun(Enum? initialStep = null)
    {
        Step = initialStep;
        IsRunning = true;
        _lastStep = null;
        _waiting = false;
        Trace?.Invoke($"{GetType().Name}: run started.");
        Changed += WakeRun;
        if (Step is not null)
            StepChanged?.Invoke();
    }

    protected void EndRun(CancellationToken cancellationToken)
    {
        Changed -= WakeRun;
        IsRunning = false;
        Step = null;
        StepChanged?.Invoke();
        Trace?.Invoke(
            $"{GetType().Name}: run ended; cancelled={cancellationToken.IsCancellationRequested}; "
                + $"last={_lastStep ?? "no step"}.");
    }
}
