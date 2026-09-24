using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;

namespace IBTM.BoltFastening;

public sealed partial class BoltFasteningStation
{
    public bool IsAtSafeZ(bool live = true)
    {
        return Motion.IsSettled(live, MotionAxis.Z)
            && Motion.IsAtZ(_settings.SafeZ, live);
    }

    internal bool IsAt(BoltPoint bolt, bool live = true, bool atSafeZ = false)
    {
        var position = _settings.GetBoltPosition(bolt);
        if (atSafeZ)
            position.Z = _settings.SafeZ;
        return IsAt(position, live);
    }

    internal bool IsAtPickupXY(bool live = true)
    {
        var target = _settings.PickupPosition;
        var current = Motion.ReadPosition(live);
        return Motion.IsSettled(live, MotionAxis.X, MotionAxis.Y)
            && Math.Abs(current.X - target.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - target.Y) <= MotionService.PositionToleranceMillimeters;
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ShootingHead.CheckReadyAsync(cancellationToken);
        await PickupHead.CheckReadyAsync(cancellationToken);
    }

    public async Task ResetHeadsAsync(CancellationToken cancellationToken = default)
    {
        List<Exception>? failures = null;
        foreach (var head in new[] { ShootingHead, PickupHead })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await head.ResetAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Bolt controller reset failed.", failures);
        }
    }

    public Task SetHeadDownAsync(
        FasteningHead head,
        bool down,
        CancellationToken cancellationToken = default)
    {
        var output = head switch
        {
            FasteningHead.Pickup => OutputIo.PickupHeadDown,
            FasteningHead.Shooting => OutputIo.ShootingHeadDown,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        return _io.SetOutputAndWaitAsync(output, down, cancellationToken);
    }

    internal Task SetPickupTableDownAsync(bool down, CancellationToken cancellationToken)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, down, cancellationToken);
    }

    public Task RaiseCylindersAsync(CancellationToken cancellationToken = default)
    {
        return Task.WhenAll(
            SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken),
            SetHeadDownAsync(FasteningHead.Shooting, false, cancellationToken));
    }

    internal Task SetShootingEscapeForwardAsync(bool forward, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, forward, cancellationToken);
    }

    internal Task WaitForShootingTubeClearAsync(CancellationToken cancellationToken = default)
    {
        return _io.WaitForInputAsync(
            InputIo.ShootingTubeBoltDetected, false, _settings.ShootingDetectionTimeoutMilliseconds, cancellationToken);
    }

    internal async Task WaitForBoltSupplyAsync(FasteningHead head, CancellationToken cancellationToken)
    {
        var boltDetected = head switch
        {
            FasteningHead.Pickup => InputIo.PickupFeederBoltDetected,
            FasteningHead.Shooting => InputIo.ShootingFeederBoltDetected,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        var changed = new AsyncAutoResetEvent();

        void OnSupplyInputChanged(InputIo input, bool value)
        {
            if (input == boltDetected)
                changed.Set();
        }

        // Subscribe before checking feedback so newly prepared supply is not missed.
        _io.InputChanged += OnSupplyInputChanged;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (!_io.GetInput(boltDetected))
            {
                await changed.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            _io.InputChanged -= OnSupplyInputChanged;
        }
    }

    public void StopShooting(Exception? operationFailure = null)
    {
        Exception? cleanupFailure = null;
        try
        {
            _io.SetOutput(OutputIo.ShootBolt, false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }
        try
        {
            _io.SetOutput(OutputIo.ShootingEscapeForward, false);
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null ? exception : new AggregateException(cleanupFailure, exception);
        }
        if (cleanupFailure is not null)
            throw operationFailure is null ? cleanupFailure : new AggregateException(operationFailure, cleanupFailure);
    }

    public void StopIoStart(FasteningHead head)
    {
        _io.SetOutput(
            head == FasteningHead.Pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart,
            false);
    }

    public async Task SetVacuumAsync(
        FasteningHead head,
        bool on,
        CancellationToken cancellationToken,
        bool waitForFeedback = true)
    {
        var output = head == FasteningHead.Pickup
            ? OutputIo.PickupHeadVacuumPump
            : OutputIo.ShootingHeadVacuumPump;
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(output, on);
        if (waitForFeedback && head == FasteningHead.Pickup)
            await _io.WaitForInputAsync(InputIo.PickupHeadVacuumDetected, on, cancellationToken);
    }

    public async Task<bool> HomeAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        if (axis != MotionAxis.Z)
            EnsureCanMoveHorizontal(cancellationToken);
        return await _motion.HomeAsync(axis, _settings.Motion.Home(axis).SearchSpeed, cancellationToken);
    }

    public async Task<bool> HomeHorizontalAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return await _motion.HomeHorizontalAsync(
            _settings.Motion.HorizontalHome.SearchSpeed,
            cancellationToken);
    }

    public async Task MoveToXYAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        await MoveToSafeZAsync(cancellationToken);
        EnsureCanMoveHorizontal(cancellationToken);
        await _motion.MoveToXYAsync(x, y, _settings.Motion.HorizontalSpeed, cancellationToken);
    }

    public Task MoveZAsync(double z, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_motion.GetAxisState(MotionAxis.Z).Homed)
            throw new MotionInterlockException("Home fastening Z before moving to a taught height.");
        return _motion.MoveAxisAsync(MotionAxis.Z, z, _settings.Motion.ZSpeed, cancellationToken);
    }

    public async Task MoveToPickupPositionAsync(CancellationToken cancellationToken = default)
    {
        await MoveToPickupXYAsync(cancellationToken);
        await MoveToPickupZAsync(cancellationToken);
    }

    public async Task ReturnFromPickupAsync(CancellationToken cancellationToken = default)
    {
        await MoveToSafeZAsync(cancellationToken);
        await SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken);
    }

    public async Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default)
    {
        switch (point)
        {
            case { Target: TeachingTarget.BoltPosition, Bolt: { } bolt }:
            {
                _log?.LogInformation(
                    "Bolt teaching Move To: {HeatSink}, bolt {Bolt}, {Head}; target X={X}, Y={Y}, Z={Z}; Safe Z={SafeZ}.",
                    bolt.HeatSink, bolt.Number, bolt.Head, position.X, position.Y, position.Z, _settings.SafeZ);
                if (!point.HasPosition)
                    throw new MotionInterlockException("Record fastening XY before moving to this bolt.");
                var tableDown = bolt.Head == FasteningHead.Pickup;
                EnsureCanMoveHorizontal(cancellationToken);
                await MoveToSafeZAsync(cancellationToken);
                _log?.LogInformation("Bolt teaching Move To: Safe Z completed; requesting pickup table {Table}.",
                    tableDown ? "DOWN" : "UP");
                EnsureCanMoveHorizontal(cancellationToken);
                await SetPickupTableDownAsync(tableDown, cancellationToken);
                _log?.LogInformation("Bolt teaching Move To: pickup table feedback confirmed.");
                using var move = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                void CheckTeachingTable()
                {
                    if (PickupTablePosition != (tableDown ? BoltCylinderState.Down : BoltCylinderState.Up))
                        move.Cancel();
                }

                Changed += CheckTeachingTable;
                try
                {
                    CheckTeachingTable();
                    EnsureCanMoveHorizontal(move.Token);
                    _log?.LogInformation("Bolt teaching Move To: requesting XY, X={X}, Y={Y}.", position.X, position.Y);
                    await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, move.Token);
                    _log?.LogInformation("Bolt teaching Move To: XY command completed; requesting fastening Z={Z}.", position.Z);
                    CheckTeachingTable();
                    EnsureCanMoveHorizontal(move.Token);
                    await MoveZAsync(position.Z, move.Token);
                    move.Token.ThrowIfCancellationRequested();
                    _log?.LogInformation("Bolt teaching Move To: fastening Z completed.");
                }
                catch (OperationCanceledException) when (move.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new MotionInterlockException(
                        $"Keep the pickup table {(tableDown ? "down" : "up")} while moving to the selected bolt's fastening position.");
                }
                finally
                {
                    Changed -= CheckTeachingTable;
                }
                break;
            }
            case { Target: TeachingTarget.BoltPickup }:
                await MoveToPickupPositionAsync(cancellationToken);
                break;
            case { Mode: TeachMode.XYOnly }:
                await MoveToXYAsync(position.X, position.Y, cancellationToken);
                break;
            case { Mode: TeachMode.ZOnly }:
                await MoveZAsync(position.Z, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    public Task AdjustAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        return _motion.AdjustAxisAsync(axis, position, velocity, cancellationToken);
    }

    public Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        return _motion.JogAsync(axis, velocity, cancellationToken);
    }

    internal async Task MoveToBoltAsync(
        BoltPoint bolt, CancellationToken cancellationToken = default)
    {
        var position = _settings.GetBoltPosition(bolt);
        _log?.LogInformation(
            "Automatic bolt move: {HeatSink}, bolt {Bolt}, {Head}; target X={X}, Y={Y}, Z={Z}.",
            bolt.HeatSink, bolt.Number, bolt.Head, position.X, position.Y, position.Z);
        // XY travel uses Safe Z. Approach the work height with both heads raised.
        await MoveToXYAsync(position.X, position.Y, cancellationToken);
        EnsureCanMoveHorizontal(cancellationToken);
        await MoveZAsync(position.Z, cancellationToken);
    }

    internal async Task MoveToPickupXYAsync(CancellationToken cancellationToken = default)
    {
        await RaiseCylindersAsync(cancellationToken);
        await MoveToSafeZAsync(cancellationToken);
        if (PickupTablePosition != BoltCylinderState.Down)
            await SetPickupTableDownAsync(true, cancellationToken);
        EnsureCanMoveHorizontal(cancellationToken);
        await _motion.MoveToXYAsync(
            _settings.PickupPosition.X,
            _settings.PickupPosition.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    internal Task MoveToPickupZAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return MoveZAsync(_settings.PickupPosition.Z, cancellationToken);
    }

    public Task MoveToSafeZAsync(CancellationToken cancellationToken = default)
    {
        return MoveZAsync(_settings.SafeZ, cancellationToken);
    }

    private bool IsAt(AxisPosition target, bool live = true)
    {
        var current = Motion.ReadPosition(live);
        return Motion.IsSettled(live, MotionAxis.X, MotionAxis.Y)
            && Motion.ReadAxisState(MotionAxis.Z, live).InPosition
            && Math.Abs(current.X - target.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - target.Y) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Z - target.Z) <= MotionService.PositionToleranceMillimeters;
    }

    private void EnsureCanMoveHorizontal(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsHorizontalMoveAllowed)
        {
            throw new MotionInterlockException("Raise both fastening heads before moving X/Y.");
        }
    }
}
