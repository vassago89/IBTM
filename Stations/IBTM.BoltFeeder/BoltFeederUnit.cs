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
        : base([
            InputIo.PickupFeederBoltDetected,
            InputIo.ShootingFeederBoltDetected,
            InputIo.ShootingEscapeForward,
            InputIo.ShootingEscapeBackward,
        ])
    {
        _io = io;
        _settings = settings;
        _units = units;
        ObserveIo(io);
    }

    public IoTimeoutException? EmptyAlarm { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken = default, FasteningHead? head = null)
    {
        EmptyAlarm = null;
        var pickupEnabled = _units.PickupBoltFeeder && head is not FasteningHead.Shooting;
        var shootingEnabled = _units.ShootingBoltFeeder && head is not FasteningHead.Pickup;
        var startedAt = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _pickupChangedAt, startedAt);
        Interlocked.Exchange(ref _shootingChangedAt, startedAt);
        Interlocked.Exchange(ref _escapeChangedAt, startedAt);
        Exception? failure = null;
        try
        {
            BeginRun();
            cancellationToken.ThrowIfCancellationRequested();
            while (!cancellationToken.IsCancellationRequested)
            {
                var waitMilliseconds = double.PositiveInfinity;
                if (pickupEnabled)
                    waitMilliseconds = CheckEmptyTimeout(InputIo.PickupFeederBoltDetected,
                        _settings.PickupTimeoutMilliseconds, cancellationToken);
                if (shootingEnabled)
                {
                    // Feed during escape travel; only the empty alarm waits for its return.
                    if (_io.GetInput(InputIo.ShootingEscapeBackward)
                        && !_io.GetInput(InputIo.ShootingEscapeForward))
                    {
                        waitMilliseconds = Math.Min(waitMilliseconds,
                            CheckEmptyTimeout(InputIo.ShootingFeederBoltDetected,
                                _settings.ShootingTimeoutMilliseconds, cancellationToken));
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

                await WaitForChangeAsync(cancellationToken,
                    double.IsFinite(waitMilliseconds) ? TimeSpan.FromMilliseconds(Math.Ceiling(waitMilliseconds)) : null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            if (exception is IoTimeoutException timeout
                && timeout.Input is InputIo.PickupFeederBoltDetected or InputIo.ShootingFeederBoltDetected)
                EmptyAlarm = timeout;
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

    private double CheckEmptyTimeout(InputIo input, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var detected = _io.GetInput(input);
        cancellationToken.ThrowIfCancellationRequested();
        if (detected || timeoutMilliseconds == Timeout.Infinite)
            return double.PositiveInfinity;
        // Read the edge time after feedback: the bolt may have just been consumed.
        var changedAt = input == InputIo.PickupFeederBoltDetected
            ? Volatile.Read(ref _pickupChangedAt)
            : Math.Max(Volatile.Read(ref _shootingChangedAt), Volatile.Read(ref _escapeChangedAt));
        var remaining = timeoutMilliseconds - Stopwatch.GetElapsedTime(changedAt).TotalMilliseconds;
        if (remaining <= 0)
            throw new IoTimeoutException(input, true, timeoutMilliseconds);
        return remaining;
    }

    public void Stop()
    {
        _io.SetOutput(OutputIo.ShootingFeederOff, true);
    }

    protected override void OnInputChanged(InputIo input, bool value)
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
        }
    }
}
