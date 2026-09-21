using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFeeder;

public sealed class BoltFeederUnit : AutoUnit
{
    private readonly IIoService _io;
    private readonly BoltFeederSettings _settings;
    private readonly FasteningHead _head;
    private readonly InputIo _boltDetected;

    public BoltFeederUnit(FasteningHead head, IIoService io, BoltFeederSettings settings)
    {
        _io = io;
        _settings = settings;
        _head = head;
        _boltDetected = head switch
        {
            FasteningHead.Pickup => InputIo.PickupFeederBoltDetected,
            FasteningHead.Shooting => InputIo.ShootingFeederBoltDetected,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        io.InputChanged += OnInputChanged;
    }

    public override event Action? Changed;

    private int TimeoutMilliseconds => _head == FasteningHead.Pickup
        ? _settings.PickupTimeoutMilliseconds : _settings.ShootingTimeoutMilliseconds;

    public BoltFeederState State
    {
        get
        {
            return _io.GetInput(_boltDetected)
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
                            await _io.WaitForInputAsync(_boltDetected, true, TimeoutMilliseconds, cancellationToken);
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

    public void Stop()
    {
        SetFeeding(false);
    }

    private void SetFeeding(bool value)
    {
        if (_head == FasteningHead.Shooting)
            _io.SetOutput(OutputIo.ShootingFeederOff, !value);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == _boltDetected)
        {
            Changed?.Invoke();
        }
    }
}
