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

    public abstract event Action? Changed;
    public event Action? StepChanged;
    public event Action<string>? Trace;

    // Selected execution step, also used to coordinate operation boundaries.
    // It never replaces physical feedback or ownership of an unfinished handoff.
    public Enum? Step => IsRunning ? DisplayStep : null;
    protected virtual Enum? DisplayStep => SequenceStep;
    // The last selected phase can retain unfinished handoff history across STOP.
    // It is never exposed as an active step outside Run.
    protected Enum? SequenceStep { get; private set; }
    public bool IsRunning { get; private set; }

    protected void OnChanged()
    {
        _stateChanged.Set();
    }

    protected void EnterStep(Enum step, string? target = null, long? workId = null, string? waitingFor = null)
    {
        if (!Equals(SequenceStep, step))
        {
            SequenceStep = step;
            if (IsRunning)
                StepChanged?.Invoke();
        }
        TraceStep(step, target, workId, waitingFor);
    }

    // Report temporary waiting conditions without replacing an unfinished sequence.
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

    protected Task WaitForChangeAsync(CancellationToken cancellationToken)
    {
        if (!_waiting && _lastStep is not null)
        {
            _waiting = true;
            Trace?.Invoke($"Waiting for feedback / work change: {_lastStep}");
        }
        return _stateChanged.WaitAsync(cancellationToken);
    }

    protected void BeginRun(Enum? initialStep = null)
    {
        SequenceStep = initialStep;
        IsRunning = true;
        _lastStep = null;
        _waiting = false;
        Trace?.Invoke($"{GetType().Name}: run started.");
        Changed += OnChanged;
    }

    protected void EndRun(CancellationToken cancellationToken)
    {
        Changed -= OnChanged;
        IsRunning = false;
        StepChanged?.Invoke();
        Trace?.Invoke(
            $"{GetType().Name}: run ended; cancelled={cancellationToken.IsCancellationRequested}; "
                + $"last={_lastStep ?? "no step"}.");
    }
}
