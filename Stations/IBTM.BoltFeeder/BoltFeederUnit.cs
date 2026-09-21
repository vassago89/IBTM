using System;
using System.Diagnostics;
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
    // Timing of the latest detection edge, not a remembered bolt-ready state.
    private long _boltDetectedAt;

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

    public BoltFeederState State => _io.GetInput(_boltDetected)
        ? BoltFeederState.BoltReady : BoltFeederState.WaitingForBolt;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Exception? failure = null;
        try
        {
            BeginRun();
            cancellationToken.ThrowIfCancellationRequested();
            if (_head == FasteningHead.Shooting)
            {
                _io.SetOutput(OutputIo.ShootingEscapeForward, false);
                Interlocked.Exchange(ref _boltDetectedAt,
                    _io.GetInput(_boltDetected) ? Stopwatch.GetTimestamp() : 0);
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = State;
                TraceStep(state, _boltDetected.ToString());
                switch (state)
                {
                    case BoltFeederState.WaitingForBolt:
                        SetFeeding(true);
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            // Keep feeding during escape travel; time out replenishment only after return.
                            timeout.CancelAfter(_head == FasteningHead.Shooting
                                && (!_io.GetInput(InputIo.ShootingEscapeBackward)
                                    || _io.GetInput(InputIo.ShootingEscapeForward))
                                ? Timeout.Infinite : TimeoutMilliseconds);
                            try
                            {
                                await WaitForChangeAsync(timeout.Token);
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                throw new IoTimeoutException(_boltDetected, true, TimeoutMilliseconds);
                            }
                        }
                        break;
                    case BoltFeederState.BoltReady when _head == FasteningHead.Shooting:
                        var boltDetectedAt = Volatile.Read(ref _boltDetectedAt);
                        var remaining = boltDetectedAt == 0 ? TimeSpan.Zero
                            : TimeSpan.FromMilliseconds(_settings.ShootingRunOnMilliseconds)
                                - Stopwatch.GetElapsedTime(boltDetectedAt);
                        SetFeeding(remaining > TimeSpan.Zero);
                        if (remaining <= TimeSpan.Zero)
                        {
                            await WaitForChangeAsync(cancellationToken);
                            break;
                        }
                        using (var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            delay.CancelAfter(remaining);
                            try
                            {
                                await WaitForChangeAsync(delay.Token);
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                // Recheck the latest detection before stopping the feeder.
                            }
                        }
                        break;
                    case BoltFeederState.BoltReady:
                        await WaitForChangeAsync(cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            EndRun(cancellationToken);
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
        if (_head == FasteningHead.Shooting && input == _boltDetected)
            Interlocked.Exchange(ref _boltDetectedAt, value ? Stopwatch.GetTimestamp() : 0);
        if (input == _boltDetected
            || _head == FasteningHead.Shooting
                && input is InputIo.ShootingEscapeForward or InputIo.ShootingEscapeBackward)
        {
            Changed?.Invoke();
        }
    }

}
