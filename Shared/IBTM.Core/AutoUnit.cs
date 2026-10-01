using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Core;

public abstract class AutoUnit
{
    private readonly AsyncAutoResetEvent _stateChanged;
    private string? _lastStep;
    private bool _waitLogged;

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
        if (IsRunning)
            WakeRun();
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
        _waitLogged = false;
        Trace.Invoke(detail);
    }

    protected Task WaitForChangeAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (!_waitLogged && _lastStep is not null)
        {
            _waitLogged = true;
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
        _waitLogged = false;
        Trace?.Invoke($"{GetType().Name}: run started.");
        if (Step is not null)
            StepChanged?.Invoke();
    }

    protected void EndRun(CancellationToken cancellationToken)
    {
        IsRunning = false;
        Step = null;
        NotifyChanged();
        StepChanged?.Invoke();
        Trace?.Invoke(
            $"{GetType().Name}: run ended; cancelled={cancellationToken.IsCancellationRequested}; "
                + $"last={_lastStep ?? "no step"}.");
    }
}

// Only known failures with material still held or supported may request a finish stop.
// Motion, I/O loss and cleanup failures remain immediate faults.
public sealed class MaintenanceStopException : InvalidOperationException
{
    public MaintenanceStopException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
