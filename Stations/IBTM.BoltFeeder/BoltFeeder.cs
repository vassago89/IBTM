using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFeeder;

public abstract class BoltFeeder : AutoUnit
{
    private readonly InputIo _boltDetected;

    protected BoltFeeder(IIoService io, InputIo boltDetected)
    {
        Io = io;
        _boltDetected = boltDetected;
        io.InputChanged += OnInputChanged;
    }

    public override event Action? Changed;

    protected IIoService Io { get; }
    protected abstract int TimeoutMilliseconds { get; }

    public BoltFeederState State
    {
        get
        {
            return Io.GetInput(_boltDetected)
                ? BoltFeederState.BoltReady
                : BoltFeederState.WaitingForBolt;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Exception? failure = null;
        try
        {
            await RunLoopAsync(ExecuteAsync, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                SetFeeding(false);
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
        }
    }

    private Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var state = State;
        TraceStep(state, _boltDetected.ToString());
        var waitingForBolt = state == BoltFeederState.WaitingForBolt;
        SetFeeding(waitingForBolt);
        return waitingForBolt
            ? Io.WaitForInputAsync(_boltDetected, true, TimeoutMilliseconds, cancellationToken)
            : WaitForChangeAsync(cancellationToken);
    }

    protected virtual void SetFeeding(bool value)
    {
    }

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input == _boltDetected)
        {
            Changed?.Invoke();
        }
    }
}
