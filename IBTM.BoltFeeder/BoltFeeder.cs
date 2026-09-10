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
        try
        {
            await RunLoopAsync(ExecuteAsync, cancellationToken);
        }
        finally
        {
            SetFeeding(false);
        }
    }

    private Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var waitingForBolt = State == BoltFeederState.WaitingForBolt;
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
