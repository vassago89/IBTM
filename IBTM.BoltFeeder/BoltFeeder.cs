using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.BoltFeeder;

public abstract class BoltFeeder
{
    private readonly InputIo _boltDetected;

    protected BoltFeeder(IIoService io, InputIo boltDetected)
    {
        Io = io;
        _boltDetected = boltDetected;
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

    protected IIoService Io { get; }
    protected abstract int TimeoutMilliseconds { get; }

    public BoltFeederState State =>
        Io.GetInput(_boltDetected)
            ? BoltFeederState.BoltReady
            : BoltFeederState.WaitingForBolt;

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var waitingForBolt =
                    State == BoltFeederState.WaitingForBolt;
                SetFeeding(waitingForBolt);
                await Io.WaitForInputAsync(
                    _boltDetected,
                    waitingForBolt,
                    waitingForBolt
                        ? TimeoutMilliseconds
                        : Timeout.Infinite,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetFeeding(false);
        }
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
