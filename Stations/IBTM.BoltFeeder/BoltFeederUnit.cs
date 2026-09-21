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
    private readonly UnitSettings _units;
    // Sensor-edge times only: empty alarms and shooting run-on do not have sequence states.
    private long _pickupChangedAt;
    private long _shootingChangedAt;
    private long _escapeChangedAt;

    public BoltFeederUnit(IIoService io, BoltFeederSettings settings, UnitSettings units)
    {
        _io = io;
        _settings = settings;
        _units = units;
        io.InputChanged += OnInputChanged;
    }

    public override event Action? Changed;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var pickupEnabled = _units.PickupBoltFeeder;
        var shootingEnabled = _units.ShootingBoltFeeder;
        var startedAt = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _pickupChangedAt, startedAt);
        Interlocked.Exchange(ref _shootingChangedAt, startedAt);
        Interlocked.Exchange(ref _escapeChangedAt, startedAt);
        Exception? failure = null;
        try
        {
            BeginRun();
            cancellationToken.ThrowIfCancellationRequested();
            if (shootingEnabled)
                _io.SetOutput(OutputIo.ShootingEscapeForward, false);
            while (!cancellationToken.IsCancellationRequested)
            {
                var waitMilliseconds = double.PositiveInfinity;
                if (pickupEnabled)
                    waitMilliseconds = CheckEmptyTimeout(InputIo.PickupFeederBoltDetected,
                        _settings.PickupTimeoutMilliseconds, Volatile.Read(ref _pickupChangedAt));
                if (shootingEnabled)
                {
                    // Feed during escape travel; only the empty alarm waits for its return.
                    if (_io.GetInput(InputIo.ShootingEscapeBackward)
                        && !_io.GetInput(InputIo.ShootingEscapeForward))
                    {
                        var emptySince = Math.Max(Volatile.Read(ref _shootingChangedAt), Volatile.Read(ref _escapeChangedAt));
                        waitMilliseconds = Math.Min(waitMilliseconds,
                            CheckEmptyTimeout(InputIo.ShootingFeederBoltDetected,
                                _settings.ShootingTimeoutMilliseconds, emptySince));
                    }
                    var runOnRemaining = _io.GetInput(InputIo.ShootingFeederBoltDetected)
                        ? _settings.ShootingRunOnMilliseconds
                            - Stopwatch.GetElapsedTime(Volatile.Read(ref _shootingChangedAt)).TotalMilliseconds
                        : double.PositiveInfinity;
                    cancellationToken.ThrowIfCancellationRequested();
                    _io.SetOutput(OutputIo.ShootingFeederOff, runOnRemaining <= 0);
                    if (runOnRemaining > 0)
                        waitMilliseconds = Math.Min(waitMilliseconds, runOnRemaining);
                }

                using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (double.IsFinite(waitMilliseconds))
                    wake.CancelAfter(TimeSpan.FromMilliseconds(waitMilliseconds));
                try
                {
                    await WaitForChangeAsync(wake.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A timer elapsed. Recheck both live inputs before alarming or stopping supply.
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
            try
            {
                if (shootingEnabled)
                    Stop();
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
            finally
            {
                EndRun(cancellationToken);
            }
        }
    }

    private double CheckEmptyTimeout(InputIo input, int timeoutMilliseconds, long changedAt)
    {
        if (_io.GetInput(input) || timeoutMilliseconds == Timeout.Infinite)
            return double.PositiveInfinity;
        var remaining = timeoutMilliseconds - Stopwatch.GetElapsedTime(changedAt).TotalMilliseconds;
        if (remaining <= 0)
            throw new IoTimeoutException(input, true, timeoutMilliseconds);
        return remaining;
    }

    public void Stop()
    {
        _io.SetOutput(OutputIo.ShootingFeederOff, true);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        switch (input)
        {
            case InputIo.PickupFeederBoltDetected:
                Interlocked.Exchange(ref _pickupChangedAt, Stopwatch.GetTimestamp());
                break;
            case InputIo.ShootingFeederBoltDetected:
                Interlocked.Exchange(ref _shootingChangedAt, Stopwatch.GetTimestamp());
                break;
            case InputIo.ShootingEscapeForward or InputIo.ShootingEscapeBackward:
                Interlocked.Exchange(ref _escapeChangedAt, Stopwatch.GetTimestamp());
                break;
            default:
                return;
        }
        Changed?.Invoke();
    }
}
