using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FASTECH;
using IBTM.Device;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Fastech;

public sealed class FastechZMotion : IAxisMotion, IMotionDiagnostics
{
    private readonly FastechController _controller;
    private readonly MotionSettings _settings;
    private readonly OperationCancellation _operations;
    private readonly MachineOptions _options;
    private readonly double _pulsesPerMillimeter;
    private int _command;

    public FastechZMotion(
        FastechController controller,
        [FromKeyedServices] FastechZAxisSettings axis,
        [FromKeyedServices] MotionSettings settings,
        MachineOptions options,
        OperationCancellation operations)
    {
        _pulsesPerMillimeter = axis.PulsesPerMillimeter;
        _controller = controller;
        _settings = settings;
        _operations = operations;
        _options = options;
        Axes = new[] { MotionAxis.Z };
    }

    public event Action<double, double, double>? PositionChanged;
    public event Action<bool>? MovingChanged;
    public event Action? StateChanged;

    public IReadOnlyList<MotionAxis> Axes { get; }
    public bool HasY => false;
    public bool HasZ => true;
    public MotionCommand Command => (MotionCommand)Volatile.Read(ref _command);

    public bool IsReady
    {
        get
        {
            _ = GetAxisState(MotionAxis.Z);
            return true;
        }
    }

    public bool IsMoving => GetAxisState(MotionAxis.Z).InMotion;
    public bool IsMovingHorizontal => false;

    public (double X, double Y, double Z) Position
    {
        get
        {
            var read = ReadDiagnosticPosition(MotionAxis.Z);
            if (read.Error is { } error)
                throw error;
            return (0, 0, read.Position!.Value);
        }
    }

    public void Initialize()
    {
        ValidatePositive(_pulsesPerMillimeter, nameof(FastechZAxisSettings.PulsesPerMillimeter));
        _controller.Initialize();
        StateChanged?.Invoke();
    }

    public AxisState GetAxisState(MotionAxis axis)
    {
        var read = ReadDiagnosticState(axis);
        if (read.Error is { } error)
            throw error;
        return read.State!.Value;
    }

    public (AxisState? State, Exception? Error) ReadDiagnosticState(MotionAxis axis)
    {
        if (axis != MotionAxis.Z)
            throw new ArgumentOutOfRangeException(nameof(axis));
        try
        {
            lock (_controller.SyncRoot)
            {
                _controller.EnsureOpen();
                var flags = 0U;
                _controller.Check(EziMOTIONPlusELib.FAS_GetAxisStatus(FastechController.BoardId, ref flags),
                    nameof(EziMOTIONPlusELib.FAS_GetAxisStatus));
                // Ezi-SERVO II Plus-E status layout (MOTION_EziSERVO2_DEFINE.h).
                return (new AxisState(
                    Homed: (flags & 0x02000000) != 0,
                    ServoOn: (flags & 0x00100000) != 0,
                    Alarm: (flags & 0x00000001) != 0,
                    InPosition: (flags & 0x00080000) != 0,
                    Emergency: (flags & 0x00010000) != 0,
                    HomeSensor: (flags & 0x00800000) != 0,
                    PositiveLimit: (flags & 0x0000000A) != 0,
                    NegativeLimit: (flags & 0x00000014) != 0,
                    InMotion: (flags & 0x08040000) != 0), null);
            }
        }
        catch (Exception error)
        {
            return (null, error);
        }
    }

    public (double? Position, Exception? Error) ReadDiagnosticPosition(MotionAxis axis)
    {
        if (axis != MotionAxis.Z)
            throw new ArgumentOutOfRangeException(nameof(axis));
        try
        {
            lock (_controller.SyncRoot)
            {
                _controller.EnsureOpen();
                var pulses = 0;
                _controller.Check(EziMOTIONPlusELib.FAS_GetActualPos(FastechController.BoardId, ref pulses),
                    nameof(EziMOTIONPlusELib.FAS_GetActualPos));
                var millimeters = pulses / _pulsesPerMillimeter;
                if (!double.IsFinite(millimeters))
                    throw new InvalidOperationException("FASTECH position scale produced a non-finite coordinate.");
                return (millimeters, null);
            }
        }
        catch (Exception error)
        {
            return (null, error);
        }
    }

    public async Task MoveAxisAsync(
        MotionAxis axis, double position, double velocity, CancellationToken cancellationToken = default,
        double? accelerationSeconds = null, double? decelerationSeconds = null)
    {
        using var operation = _operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        if (axis != MotionAxis.Z)
            throw new ArgumentOutOfRangeException(nameof(axis));
        var target = Math.Round(position * _pulsesPerMillimeter);
        if (!double.IsFinite(target) || target < int.MinValue || target > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(position), "Position exceeds the FASTECH pulse range.");
        EnsureStopped();
        var speed = ToSpeed(velocity);
        var options = new EziMOTIONPlusELib.MOTION_OPTION_EX
        {
            BIT_USE_CUSTOMACCEL = true,
            BIT_USE_CUSTOMDECEL = true,
            wCustomAccelTime = ToMilliseconds(accelerationSeconds
                ?? _settings.ZAccelerationSeconds ?? _settings.AccelerationSeconds),
            wCustomDecelTime = ToMilliseconds(decelerationSeconds
                ?? _settings.ZDecelerationSeconds ?? _settings.DecelerationSeconds),
        };
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            BeginMotion(MotionCommand.Positioning);
            await Task.Run(() =>
            {
                lock (_controller.SyncRoot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _controller.EnsureOpen();
                    _controller.Check(EziMOTIONPlusELib.FAS_MoveSingleAxisAbsPosEx(
                        FastechController.BoardId, (int)target, speed, options),
                        nameof(EziMOTIONPlusELib.FAS_MoveSingleAxisAbsPosEx));
                }
            }).ConfigureAwait(false);
            await WaitForMoveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await StopAndWaitAsync(error).ConfigureAwait(false);
            throw;
        }
        finally
        {
            EndMotion();
        }
    }

    public async Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        if (axis != MotionAxis.Z)
            throw new ArgumentOutOfRangeException(nameof(axis));
        EnsureStopped();
        var speed = ToSpeed(Math.Abs(velocity));
        var options = new EziMOTIONPlusELib.VELOCITY_OPTION_EX
        {
            BIT_USE_CUSTOMACCDEC = true,
            wCustomAccDecTime = ToMilliseconds(_settings.ZAccelerationSeconds ?? _settings.AccelerationSeconds),
        };
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            BeginMotion(MotionCommand.Adjustment);
            await Task.Run(() =>
            {
                lock (_controller.SyncRoot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _controller.EnsureOpen();
                    _controller.Check(EziMOTIONPlusELib.FAS_MoveVelocityEx(
                        FastechController.BoardId, speed, velocity > 0 ? 1 : 0, options),
                        nameof(EziMOTIONPlusELib.FAS_MoveVelocityEx));
                }
            }).ConfigureAwait(false);
            await WaitForMoveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await StopAndWaitAsync(error).ConfigureAwait(false);
            throw;
        }
        finally
        {
            EndMotion();
        }
    }

    public async Task<bool> HomeAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        if (axis != MotionAxis.Z)
            throw new ArgumentOutOfRangeException(nameof(axis));
        EnsureStopped();
        var speed = ToSpeed(velocity);
        var searchSpeed = ToSpeed(_settings.ZHome.DetectionSpeed);
        if (speed > int.MaxValue || searchSpeed > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(velocity), "Home speed exceeds the FASTECH parameter range.");
        var acceleration = ToMilliseconds(_settings.ZHome.SearchAccelerationSeconds);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            BeginMotion(MotionCommand.Positioning);
            await Task.Run(() =>
            {
                lock (_controller.SyncRoot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _controller.EnsureOpen();
                    // Plus-E SERVO2_ORGSPEED / ORGSEARCHSPEED / ORGACCDECTIME.
                    _controller.Check(EziMOTIONPlusELib.FAS_SetParameter(FastechController.BoardId, 14, (int)speed),
                        nameof(EziMOTIONPlusELib.FAS_SetParameter));
                    _controller.Check(EziMOTIONPlusELib.FAS_SetParameter(FastechController.BoardId, 15, (int)searchSpeed),
                        nameof(EziMOTIONPlusELib.FAS_SetParameter));
                    _controller.Check(EziMOTIONPlusELib.FAS_SetParameter(FastechController.BoardId, 16, acceleration),
                        nameof(EziMOTIONPlusELib.FAS_SetParameter));
                    // S2 backup: Limit Origin (2), negative / CCW (1). No origin sensor.
                    _controller.Check(EziMOTIONPlusELib.FAS_SetParameter(FastechController.BoardId, 17, 2),
                        nameof(EziMOTIONPlusELib.FAS_SetParameter));
                    _controller.Check(EziMOTIONPlusELib.FAS_SetParameter(FastechController.BoardId, 18, 1),
                        nameof(EziMOTIONPlusELib.FAS_SetParameter));
                    cancellationToken.ThrowIfCancellationRequested();
                    _controller.Check(EziMOTIONPlusELib.FAS_MoveOriginSingleAxis(FastechController.BoardId),
                        nameof(EziMOTIONPlusELib.FAS_MoveOriginSingleAxis));
                }
            }).ConfigureAwait(false);
            await WaitForMoveAsync(cancellationToken, homing: true).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            await StopAndWaitAsync(error).ConfigureAwait(false);
            throw;
        }
        finally
        {
            EndMotion();
        }
    }

    private async Task WaitForMoveAsync(CancellationToken cancellationToken, bool homing = false)
    {
        // The manufacturer's move and origin samples wait 1 ms before the first status read.
        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        long? stoppedAt = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = GetAxisState(MotionAxis.Z);
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Alarm || state.Emergency || !state.ServoOn)
                throw new MotionInterlockException("FASTECH Z motion stopped by an axis fault or servo OFF.");
            if (!state.InMotion && state.InPosition && (!homing || state.Homed))
            {
                PositionChanged?.Invoke(0, 0, Position.Z);
                return;
            }
            if (state.InMotion)
                stoppedAt = null;
            else
            {
                stoppedAt ??= Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(stoppedAt.Value).TotalMilliseconds >= _options.TimeoutMilliseconds)
                    throw new MotionException("Move FASTECH Z",
                        new TimeoutException("FASTECH Z completion feedback was not received."));
            }
            await Task.Delay(_options.MotionPollMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StopAndWaitAsync(Exception cause)
    {
        List<Exception> failures = [cause];
        try
        {
            await Task.Run(Stop).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        try
        {
            var started = Stopwatch.GetTimestamp();
            while (IsMoving)
            {
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= _options.TimeoutMilliseconds)
                    throw new TimeoutException("FASTECH Z stop feedback was not received.");
                await Task.Delay(_options.MotionPollMilliseconds).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        if (failures.Count > 1)
            throw new MotionException("Stop FASTECH Z", new AggregateException(failures));
    }

    public void Stop()
    {
        lock (_controller.SyncRoot)
        {
            if (!_controller.IsOpen)
                return;
            _controller.Check(EziMOTIONPlusELib.FAS_MoveStop(FastechController.BoardId),
                nameof(EziMOTIONPlusELib.FAS_MoveStop));
        }
    }

    public void SetServo(MotionAxis axis, bool on)
    {
        if (axis != MotionAxis.Z)
            throw new ArgumentOutOfRangeException(nameof(axis));
        lock (_controller.SyncRoot)
        {
            _controller.EnsureOpen();
            _controller.Check(EziMOTIONPlusELib.FAS_ServoEnable(FastechController.BoardId, on ? 1 : 0),
                nameof(EziMOTIONPlusELib.FAS_ServoEnable));
        }
        StateChanged?.Invoke();
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() =>
        {
            lock (_controller.SyncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = GetAxisState(MotionAxis.Z);
                cancellationToken.ThrowIfCancellationRequested();
                if (state.Alarm)
                    _controller.Check(EziMOTIONPlusELib.FAS_ServoAlarmReset(FastechController.BoardId),
                        nameof(EziMOTIONPlusELib.FAS_ServoAlarmReset));
            }
        }).ConfigureAwait(false);
        StateChanged?.Invoke();
    }

    private void EnsureStopped()
    {
        if (Command != MotionCommand.None || IsMoving)
            throw new MotionInterlockException("Wait for Z to stop before moving.");
    }

    private void BeginMotion(MotionCommand command)
    {
        Volatile.Write(ref _command, (int)command);
        MovingChanged?.Invoke(true);
        StateChanged?.Invoke();
    }

    private void EndMotion()
    {
        Volatile.Write(ref _command, (int)MotionCommand.None);
        MovingChanged?.Invoke(false);
        StateChanged?.Invoke();
    }

    private static void ValidatePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be positive and finite.");
    }

    private uint ToSpeed(double millimetersPerSecond)
    {
        ValidatePositive(millimetersPerSecond, nameof(millimetersPerSecond));
        var speed = Math.Round(millimetersPerSecond * _pulsesPerMillimeter);
        if (!double.IsFinite(speed) || speed < 1 || speed > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(millimetersPerSecond), "Speed must be within 1–4294967295 pulses per second.");
        return (uint)speed;
    }

    private static ushort ToMilliseconds(double seconds)
    {
        ValidatePositive(seconds, nameof(seconds));
        var milliseconds = Math.Round(seconds * 1000);
        if (milliseconds < 1 || milliseconds > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Time must be within 1–65535 milliseconds.");
        return (ushort)milliseconds;
    }
}
