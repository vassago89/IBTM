using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Core;

public abstract class AutoUnit
{
    private readonly AsyncAutoResetEvent _stateChanged = new();
    private string? _lastStep;
    private bool _waiting;

    public abstract event Action? Changed;
    public event Action<string>? Trace;

    protected void TraceStep(Enum step, string? target = null, long? workId = null, string? waitingFor = null)
    {
        if (Trace is null)
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

    protected async Task RunLoopAsync(
        Func<CancellationToken, Task> execute,
        CancellationToken cancellationToken,
        Func<bool>? completed = null)
    {
        _lastStep = null;
        _waiting = false;
        Trace?.Invoke($"{GetType().Name}: run started.");
        Changed += _stateChanged.Set;
        try
        {
            while (!cancellationToken.IsCancellationRequested && completed?.Invoke() != true)
            {
                await execute(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Changed -= _stateChanged.Set;
            Trace?.Invoke(
                $"{GetType().Name}: run ended; cancelled={cancellationToken.IsCancellationRequested}; "
                    + $"last={_lastStep ?? "no step"}.");
        }
    }
}
