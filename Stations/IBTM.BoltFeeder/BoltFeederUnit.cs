using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFeeder;

public sealed class BoltFeederUnit : AutoUnit
{
    private readonly BoltFeederSettings _settings;
    private readonly UnitSettings _units;
    // Sensor-edge times only: empty alarms and shooting run-on do not have sequence states.
    private long _pickupChangedAt;
    private long _shootingChangedAt;
    private long _escapeChangedAt;

    public BoltFeederUnit(IIoService io, BoltFeederSettings settings, UnitSettings units)
        : base(io, [
            InputIo.PickupFeederBoltDetected,
            InputIo.ShootingFeederBoltDetected,
            InputIo.ShootingEscapeForward,
            InputIo.ShootingEscapeBackward,
        ])
    {
        _settings = settings;
        _units = units;
    }

    public IoTimeoutException? PickupEmptyAlarm { get; private set; }
    public IoTimeoutException? ShootingEmptyAlarm { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken = default, FasteningHead? head = null)
    {
        PickupEmptyAlarm = null;
        ShootingEmptyAlarm = null;
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
                if (pickupEnabled && PickupEmptyAlarm is null)
                    waitMilliseconds = CheckEmptyTimeout(InputIo.PickupFeederBoltDetected,
                        _settings.PickupTimeoutMilliseconds, cancellationToken);
                if (shootingEnabled && ShootingEmptyAlarm is null)
                {
                    // Feed during escape travel; only the empty alarm waits for its return.
                    if (Io.GetInput(InputIo.ShootingEscapeBackward)
                        && !Io.GetInput(InputIo.ShootingEscapeForward))
                    {
                        waitMilliseconds = Math.Min(waitMilliseconds,
                            CheckEmptyTimeout(InputIo.ShootingFeederBoltDetected,
                                _settings.ShootingTimeoutMilliseconds, cancellationToken));
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ShootingEmptyAlarm is not null)
                        Stop();
                    else
                    {
                        var runOnRemaining = Io.GetInput(InputIo.ShootingFeederBoltDetected)
                            ? _settings.ShootingRunOnMilliseconds
                                - Stopwatch.GetElapsedTime(Volatile.Read(ref _shootingChangedAt)).TotalMilliseconds
                            : double.PositiveInfinity;
                        cancellationToken.ThrowIfCancellationRequested();
                        Io.SetOutput(OutputIo.ShootingFeederOff, runOnRemaining <= 0);
                        if (runOnRemaining > 0)
                            waitMilliseconds = Math.Min(waitMilliseconds, runOnRemaining);
                    }
                }

                // Keep the healthy feeder running while the current carrier finishes its work.
                if ((!pickupEnabled || PickupEmptyAlarm is not null)
                    && (!shootingEnabled || ShootingEmptyAlarm is not null)
                    && (PickupEmptyAlarm ?? ShootingEmptyAlarm) is { } alarm)
                    throw alarm;

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
        var detected = Io.GetInput(input);
        cancellationToken.ThrowIfCancellationRequested();
        if (detected || timeoutMilliseconds == Timeout.Infinite)
            return double.PositiveInfinity;
        // Read the edge time after feedback: the bolt may have just been consumed.
        var changedAt = input == InputIo.PickupFeederBoltDetected
            ? Volatile.Read(ref _pickupChangedAt)
            : Math.Max(Volatile.Read(ref _shootingChangedAt), Volatile.Read(ref _escapeChangedAt));
        var remaining = timeoutMilliseconds - Stopwatch.GetElapsedTime(changedAt).TotalMilliseconds;
        if (remaining > 0)
            return remaining;

        var alarm = new IoTimeoutException(input, true, timeoutMilliseconds);
        if (input == InputIo.PickupFeederBoltDetected)
            PickupEmptyAlarm = alarm;
        else
            ShootingEmptyAlarm = alarm;
        NotifyChanged();
        return double.PositiveInfinity;
    }

    public void Stop()
    {
        Io.SetOutput(OutputIo.ShootingFeederOff, true);
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
