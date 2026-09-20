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
            BeginRun();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var state = State;
                    TraceStep(state, _boltDetected.ToString());
                    switch (state)
                    {
                        case BoltFeederState.WaitingForBolt:
                            SetFeeding(true);
                            await Io.WaitForInputAsync(_boltDetected, true, TimeoutMilliseconds, cancellationToken);
                            break;
                        case BoltFeederState.BoltReady:
                            SetFeeding(false);
                            await WaitForChangeAsync(cancellationToken);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                EndRun(cancellationToken);
            }
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

    protected virtual void SetFeeding(bool value)
    {
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == _boltDetected)
        {
            Changed?.Invoke();
        }
    }
}
