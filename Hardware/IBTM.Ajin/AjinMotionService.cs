using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Ajin;

public class AjinMotionService : MotionService, IMotionDiagnostics
{
    private const uint HomeSuccess = 0x01;
    private const uint HomeSearching = 0x02;
    private const int PositiveLimitBit = 0;
    private const int NegativeLimitBit = 1;
    private const int AlarmBit = 4;
    private const int InPositionBit = 5;
    private const int EmergencyBit = 6;
    private const int HomeSensorBit = 7;
    private const uint AccelerationInUnitsPerSecondSquared = 0;
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly AjinController _controller;
    private readonly MachineOptions _options;
    private readonly int _axisX;
    private readonly int? _axisY;
    private readonly int? _axisZ;
    private readonly Dictionary<int, (double Unit, int Pulse, HomeDirection HomeDirection)> _axisParameters;

    public AjinMotionService(
        AjinController controller,
        AxisHardware axisX,
        AxisHardware? axisY,
        AxisHardware? axisZ,
        MotionSettings settings,
        MachineOptions options,
        OperationCancellation operationCancellation,
        Func<double>? horizontalZ)
        : base(
            settings,
            operationCancellation,
            hasY: axisY is not null,
            hasZ: axisZ is not null,
            horizontalZ: horizontalZ)
    {
        _controller = controller;
        _options = options;
        _axisX = axisX.Number;
        _axisY = axisY?.Number;
        _axisZ = axisZ?.Number;
        _axisParameters = new[] { axisX, axisY, axisZ }
            .OfType<AxisHardware>()
            .ToDictionary(axis => axis.Number, axis => (axis.MoveUnit, axis.MovePulse, axis.HomeDirection));
    }

    public override bool IsReady
    {
        get
        {
            return _axisParameters.Keys.All(DoAxisParametersMatch);
        }
    }

    public override bool IsMoving
    {
        get
        {
            return Axes.Any(axis => GetAxisState(axis).InMotion);
        }
    }

    public override bool IsMovingHorizontal
    {
        get
        {
            return Axes.Any(axis => axis != MotionAxis.Z && GetAxisState(axis).InMotion);
        }
    }

    public override void Initialize()
    {
        _controller.Initialize();
        foreach (var axis in _axisParameters.Keys)
        {
            if (DoAxisParametersMatch(axis))
            {
                continue;
            }

            var inMotion = 0U;
            AjinController.Check(
                CAXM.AxmStatusReadInMotion(axis, ref inMotion),
                $"{nameof(CAXM.AxmStatusReadInMotion)} (axis={axis})");
            if (inMotion != 0)
            {
                throw new MotionInterlockException(
                    $"Cannot change AJIN axis {axis} unit settings: AxmStatusReadInMotion={inMotion}.");
            }

            var scale = _axisParameters[axis];
            AjinController.Check(
                CAXM.AxmMotSetMoveUnitPerPulse(axis, scale.Unit, scale.Pulse),
                $"{nameof(CAXM.AxmMotSetMoveUnitPerPulse)} (axis={axis})");
            AjinController.Check(
                CAXM.AxmMotSetAccelUnit(axis, AccelerationInUnitsPerSecondSquared),
                $"{nameof(CAXM.AxmMotSetAccelUnit)} (axis={axis})");
        }

        // Communication readiness is independent of servo power and axis alarms.
        // Servo ON belongs to an explicit operator command (or the existing RESET flow).
        PublishPosition();
        PublishStateChanged();
    }

    protected override Task MoveXYCoreAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        var axisYNumber = _axisY!.Value;
        var position = GetPosition();
        var distanceX = Math.Abs(x - position.X);
        var distanceY = Math.Abs(y - position.Y);

        if (distanceX == 0 && distanceY == 0)
        {
            return Task.CompletedTask;
        }

        if (distanceX == 0)
        {
            return MoveAxisCoreAsync(MotionAxis.Y, y, velocity, cancellationToken);
        }

        if (distanceY == 0)
        {
            return MoveAxisCoreAsync(MotionAxis.X, x, velocity, cancellationToken);
        }

        var totalDistance = Math.Sqrt(distanceX * distanceX + distanceY * distanceY);
        var velocityX = ToUnits(velocity * distanceX / totalDistance);
        var velocityY = ToUnits(velocity * distanceY / totalDistance);
        var axes = new[] { _axisX, axisYNumber };

        return RunMoveAsync(
            () => CAXM.AxmMoveMultiPos(
                axes.Length,
                axes,
                [ToUnits(x), ToUnits(y)],
                [velocityX, velocityY],
                [velocityX / Settings.AccelerationSeconds, velocityY / Settings.AccelerationSeconds],
                [velocityX / Settings.DecelerationSeconds, velocityY / Settings.DecelerationSeconds]),
            nameof(CAXM.AxmMoveMultiPos),
            axes,
            [x, y],
            cancellationToken);
    }

    protected override Task JogCoreAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken)
    {
        var axisNumber = GetAxis(axis);
        var velocityInUnits = ToUnits(velocity);
        var acceleration = Math.Abs(velocityInUnits) / Settings.AccelerationSeconds;
        var deceleration = Math.Abs(velocityInUnits) / Settings.DecelerationSeconds;
        return RunMoveAsync(
            () => CAXM.AxmMoveVel(axisNumber, velocityInUnits, acceleration, deceleration),
            nameof(CAXM.AxmMoveVel),
            [axisNumber],
            null,
            cancellationToken);
    }

    public override void SetServo(MotionAxis axis, bool on)
    {
        var axisNumber = GetAxis(axis);
        AjinController.Check(
            CAXM.AxmSignalServoOn(axisNumber, on ? 1U : 0U),
            $"{nameof(CAXM.AxmSignalServoOn)} (axis={axisNumber}, on={on})");
        PublishStateChanged();
    }

    public override (double X, double Y, double Z) GetPosition()
    {
        return (
            ReadPosition(_axisX),
            _axisY is null ? 0 : ReadPosition(_axisY.Value),
            _axisZ is null ? 0 : ReadPosition(_axisZ.Value));
    }

    public override AxisState GetAxisState(MotionAxis axis)
    {
        var read = ReadDiagnosticState(axis);
        if (read.Error is { } error)
            throw error;
        return read.State!.Value;
    }

    // These getters never initialize the motion, change parameters, reset alarms or enable servos.
    // Disabled groups can therefore be monitored through the already-open AXL connection.
    public (AxisState? State, Exception? Error) ReadDiagnosticState(MotionAxis axis)
    {
        var axisNumber = GetAxis(axis);
        var mechanical = 0U;
        var homeResult = 0U;
        var servoOn = 0U;
        var inMotion = 0U;
        var error = ReadError(
            CAXM.AxmStatusReadMechanical(axisNumber, ref mechanical),
            nameof(CAXM.AxmStatusReadMechanical), axisNumber);
        if (error is not null)
            return (null, error);
        error = ReadError(
            CAXM.AxmHomeGetResult(axisNumber, ref homeResult),
            nameof(CAXM.AxmHomeGetResult), axisNumber);
        if (error is not null)
            return (null, error);
        error = ReadError(
            CAXM.AxmSignalIsServoOn(axisNumber, ref servoOn),
            nameof(CAXM.AxmSignalIsServoOn), axisNumber);
        if (error is not null)
            return (null, error);
        error = ReadError(
            CAXM.AxmStatusReadInMotion(axisNumber, ref inMotion),
            nameof(CAXM.AxmStatusReadInMotion), axisNumber);
        if (error is not null)
            return (null, error);

        return (new AxisState(
            Homed: homeResult == HomeSuccess,
            ServoOn: servoOn != 0,
            Alarm: IsBitSet(mechanical, AlarmBit),
            InPosition: IsBitSet(mechanical, InPositionBit),
            Emergency: IsBitSet(mechanical, EmergencyBit),
            HomeSensor: IsBitSet(mechanical, HomeSensorBit),
            PositiveLimit: IsBitSet(mechanical, PositiveLimitBit),
            NegativeLimit: IsBitSet(mechanical, NegativeLimitBit),
            InMotion: inMotion != 0), null);
    }

    public (double? Position, Exception? Error) ReadDiagnosticPosition(MotionAxis axis)
    {
        return ReadPositionFeedback(GetAxis(axis));
    }

    protected override async Task<bool> HomeCoreAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        return await HomeAxesAsync([axis], velocity, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<bool> HomeHorizontalCoreAsync(
        double velocity,
        CancellationToken cancellationToken)
    {
        MotionAxis[] axes = _axisY is null ? [MotionAxis.X] : [MotionAxis.X, MotionAxis.Y];
        return await HomeAxesAsync(axes, velocity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HomeAxesAsync(
        MotionAxis[] axes,
        double velocity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var axisNumbers = axes.Select(GetAxis).ToArray();
        foreach (var axis in axes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfigureHome(axis, velocity);
        }

        var horizontal = axes.Any(axis => axis != MotionAxis.Z);
        Exception? cancellationFailure = null;
        void StopOnCancellation()
        {
            try
            {
                StopAxes(axisNumbers);
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }
        }

        Exception? failure = null;
        var homed = false;
        using (cancellationToken.Register(StopOnCancellation))
        {
            try
            {
                BeginMotion(horizontal);
                // Start every axis before waiting. One HOME operation owns X/Y cancellation and cleanup.
                foreach (var axisNumber in axisNumbers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AjinController.Check(
                        CAXM.AxmHomeSetStart(axisNumber),
                        $"{nameof(CAXM.AxmHomeSetStart)} (axis={axisNumber})");
                }

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var allHomed = true;
                    List<Exception>? homeFailures = null;
                    foreach (var axisNumber in axisNumbers)
                    {
                        var homeResult = 0U;
                        AjinController.Check(
                            CAXM.AxmHomeGetResult(axisNumber, ref homeResult),
                            $"{nameof(CAXM.AxmHomeGetResult)} (axis={axisNumber})");
                        if (homeResult == HomeSuccess)
                            continue;
                        allHomed = false;
                        if (homeResult != HomeSearching)
                        {
                            (homeFailures ??= []).Add(new System.IO.IOException(
                                $"AJIN home failed (axis={axisNumber}): {(AXT_MOTION_HOME_RESULT)homeResult} (0x{homeResult:X2})."));
                        }
                    }

                    if (homeFailures is { Count: 1 })
                        throw homeFailures[0];
                    if (homeFailures is not null)
                        throw new MotionException("Home axes", new AggregateException(homeFailures));

                    PublishPosition();
                    if (allHomed)
                    {
                        homed = true;
                        break;
                    }
                    await Task.Delay(StatusPollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (!homed)
            {
                try
                {
                    StopAxes(axisNumbers);
                }
                catch (Exception stopFailure)
                {
                    failure = new MotionException("Stop home", new AggregateException(failure!, stopFailure));
                }
            }

            failure = await EndMotionAsync(axisNumbers, horizontal, failure).ConfigureAwait(false);
        }

        // Registration disposal joins any STOP callback before its failure is collected.
        if (cancellationFailure is not null)
        {
            failure = failure is null
                ? cancellationFailure
                : new MotionException("Cancel home", new AggregateException(failure, cancellationFailure));
        }
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
        return homed;
    }

    private void ConfigureHome(MotionAxis axis, double velocity)
    {
        var axisNumber = GetAxis(axis);
        EnsureAxisParameters(axisNumber);
        var velocityInUnits = ToUnits(velocity);
        var home = Settings.Home(axis);
        var direction = 0;
        var signal = 0U;
        var zPhase = 0U;
        var clearTime = 0d;
        var offset = 0d;
        AjinController.Check(
            CAXM.AxmHomeGetMethod(
                axisNumber, ref direction, ref signal, ref zPhase, ref clearTime, ref offset),
            $"{nameof(CAXM.AxmHomeGetMethod)} (axis={axisNumber})");
        direction = _axisParameters[axisNumber].HomeDirection switch
        {
            HomeDirection.Negative => 0, // AJIN DIR_CCW
            HomeDirection.Positive => 1, // AJIN DIR_CW
            var value => throw new InvalidOperationException($"Invalid home direction for axis {axisNumber}: {value}."),
        };
        AjinController.Check(
            CAXM.AxmHomeSetMethod(axisNumber, direction, signal, zPhase, clearTime, offset),
            $"{nameof(CAXM.AxmHomeSetMethod)} (axis={axisNumber})");
        AjinController.Check(
            CAXM.AxmHomeSetVel(
                axisNumber,
                velocityInUnits,
                ToUnits(home.DetectionSpeed),
                ToUnits(home.ApproachSpeed),
                ToUnits(home.FineSpeed),
                velocityInUnits / home.SearchAccelerationSeconds,
                ToUnits(home.DetectionSpeed) / home.DetectionAccelerationSeconds),
            $"{nameof(CAXM.AxmHomeSetVel)} (axis={axisNumber})");
    }

    protected override void ResetAlarm()
    {
        foreach (var axis in _axisParameters.Keys)
        {
            AjinController.Check(
                CAXM.AxmSignalServoAlarmReset(axis, 1),
                $"{nameof(CAXM.AxmSignalServoAlarmReset)} (axis={axis})");
        }

        PublishStateChanged();
    }

    protected override Task MoveAxisCoreAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken)
    {
        var axisNumber = GetAxis(axis);
        var velocityInUnits = ToUnits(velocity);
        var acceleration = velocityInUnits / Settings.AccelerationSeconds;
        var deceleration = velocityInUnits / Settings.DecelerationSeconds;

        return RunMoveAsync(
            () => CAXM.AxmMovePos(
                axisNumber,
                ToUnits(position),
                velocityInUnits,
                acceleration,
                deceleration),
            nameof(CAXM.AxmMovePos),
            [axisNumber],
            [position],
            cancellationToken);
    }

    private async Task RunMoveAsync(
        Func<uint> move,
        string operation,
        int[] axes,
        double[]? targets,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var axis in axes)
        {
            EnsureAxisParameters(axis);
        }

        var horizontal = Array.Exists(axes, axis => axis != _axisZ);
        Exception? cancellationFailure = null;
        void StopOnCancellation()
        {
            try
            {
                StopAxes(_axisParameters.Keys);
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }
        }

        Exception? failure = null;
        using (cancellationToken.Register(StopOnCancellation))
        {
            try
            {
                BeginMotion(horizontal);
                cancellationToken.ThrowIfCancellationRequested();
                AjinController.Check(move(), operation);
                await WaitForMoveAsync(axes, cancellationToken).ConfigureAwait(false);
                if (targets is not null)
                {
                    for (var index = 0; index < axes.Length; index++)
                    {
                        var actual = ReadPosition(axes[index]);
                        if (Math.Abs(actual - targets[index]) > PositionToleranceMillimeters)
                        {
                            throw new InvalidOperationException(
                                $"Axis stopped before reaching its target (axis={axes[index]}, "
                                + $"target={targets[index]:F3}, actual={actual:F3} mm).");
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                failure = exception is OperationCanceledException
                    ? exception
                    : new MotionException(operation, exception);
                try
                {
                    StopAxes(_axisParameters.Keys);
                }
                catch (Exception stopFailure)
                {
                    failure = new MotionException("Stop motion", new AggregateException(failure, stopFailure));
                }
            }

            failure = await EndMotionAsync(axes, horizontal, failure).ConfigureAwait(false);
        }

        // Keep STOP failures on the motion task, not on the input monitor that canceled it.
        if (cancellationFailure is not null)
        {
            failure = failure is null
                ? cancellationFailure
                : new MotionException("Cancel motion", new AggregateException(failure, cancellationFailure));
        }
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private async Task<Exception?> EndMotionAsync(int[] axes, bool horizontal, Exception? failure)
    {
        Exception? cleanupFailure = null;
        try
        {
            await WaitForStopAsync(axes).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = new MotionException("Wait for motion stop", exception);
        }

        try
        {
            EndMotion(horizontal);
            PublishPosition();
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null
                ? exception
                : new AggregateException(cleanupFailure, exception);
        }

        if (cleanupFailure is not null)
        {
            return new MotionException(
                "Finish motion",
                failure is null ? cleanupFailure : new AggregateException(failure, cleanupFailure));
        }
        return failure;
    }

    protected async Task WaitForStopAsync(int[] axes)
    {
        var started = Stopwatch.GetTimestamp();
        // Stop is already requested. Cancellation must not skip its hardware acknowledgement.
        while (ReadMoveState(axes).Moving)
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= _options.TimeoutMilliseconds)
                throw new TimeoutException(
                    $"Motion did not stop within {_options.TimeoutMilliseconds} ms.");
            await Task.Delay(StatusPollInterval).ConfigureAwait(false);
        }
    }

    protected async Task WaitForMoveAsync(int[] axes, CancellationToken cancellationToken)
    {
        long? stoppedAt = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (moving, inPosition, faulted) = ReadMoveState(axes);
            if (faulted)
                throw new InvalidOperationException("Motion stopped by an axis fault.");
            if (!moving && inPosition)
                return;

            if (moving)
                stoppedAt = null;
            else
            {
                stoppedAt ??= Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(stoppedAt.Value).TotalMilliseconds >= _options.TimeoutMilliseconds)
                    throw new TimeoutException(
                        $"In-position feedback was not received within {_options.TimeoutMilliseconds} ms.");
            }

            await Task.Delay(StatusPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    protected virtual (bool Moving, bool InPosition, bool Faulted) ReadMoveState(int[] axes)
    {
        var moving = false;
        var inPosition = true;
        var faulted = false;
        foreach (var axis in axes)
        {
            var inMotion = 0U;
            var mechanical = 0U;
            AjinController.Check(
                CAXM.AxmStatusReadInMotion(axis, ref inMotion),
                nameof(CAXM.AxmStatusReadInMotion));
            AjinController.Check(
                CAXM.AxmStatusReadMechanical(axis, ref mechanical),
                nameof(CAXM.AxmStatusReadMechanical));
            moving |= inMotion != 0;
            inPosition &= IsBitSet(mechanical, InPositionBit);
            faulted |= IsBitSet(mechanical, AlarmBit) || IsBitSet(mechanical, EmergencyBit);
        }

        PublishPosition();
        return (moving, inPosition, faulted);
    }

    public override void Stop()
    {
        StopAxes(_axisParameters.Keys);
    }

    private void StopAxes(IEnumerable<int> axes)
    {
        List<Exception>? failures = null;
        foreach (var axis in axes)
        {
            try
            {
                AjinController.Check(CAXM.AxmMoveSStop(axis), $"{nameof(CAXM.AxmMoveSStop)} (axis {axis})");
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
            throw new MotionException("Stop axes", new AggregateException(failures));
    }

    private double ReadPosition(int axis)
    {
        var read = ReadPositionFeedback(axis);
        if (read.Error is { } error)
            throw error;
        return read.Position!.Value;
    }

    private (double? Position, Exception? Error) ReadPositionFeedback(int axis)
    {
        var position = 0.0;
        var error = ReadError(
            CAXM.AxmStatusGetActPos(axis, ref position),
            nameof(CAXM.AxmStatusGetActPos), axis);
        if (error is not null)
            return (null, error);
        var unit = 0.0;
        var pulse = 0;
        error = ReadError(
            CAXM.AxmMotGetMoveUnitPerPulse(axis, ref unit, ref pulse),
            nameof(CAXM.AxmMotGetMoveUnitPerPulse), axis);
        if (error is not null)
            return (null, error);
        if (!double.IsFinite(unit) || unit <= 0 || pulse <= 0)
        {
            return (null, new System.IO.IOException(
                $"Invalid AJIN position scale (axis={axis}, unit={unit}, pulse={pulse})."));
        }

        var millimeters = FromUnits(axis, position, unit, pulse);
        if (!double.IsFinite(millimeters))
        {
            return (null, new System.IO.IOException(
                $"Invalid AJIN position (axis={axis}, position={position}, millimeters={millimeters})."));
        }

        return (millimeters, null);
    }

    private static Exception? ReadError(uint result, string operation, int axis)
    {
        if (result == (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
            return null;
        return new System.IO.IOException(
            $"{operation} (axis={axis}) failed with Ajin result {(AXT_FUNC_RESULT)result} (0x{result:X8}).");
    }

    private bool DoAxisParametersMatch(int axis)
    {
        var unit = 0.0;
        var pulse = 0;
        var accelerationUnit = uint.MaxValue;
        AjinController.Check(
            CAXM.AxmMotGetMoveUnitPerPulse(axis, ref unit, ref pulse),
            $"{nameof(CAXM.AxmMotGetMoveUnitPerPulse)} (axis={axis})");
        AjinController.Check(
            CAXM.AxmMotGetAccelUnit(axis, ref accelerationUnit),
            $"{nameof(CAXM.AxmMotGetAccelUnit)} (axis={axis})");
        var expected = _axisParameters[axis];
        return unit == expected.Unit
            && pulse == expected.Pulse
            && accelerationUnit == AccelerationInUnitsPerSecondSquared;
    }

    private void EnsureAxisParameters(int axis)
    {
        if (!DoAxisParametersMatch(axis))
        {
            throw new InvalidOperationException(
                $"AJIN axis {axis} unit settings changed. Initialize motion before issuing a move.");
        }
    }

    private static double ToUnits(double millimeters)
    {
        return millimeters * 1000;
    }

    private double FromUnits(int axis, double position, double unit, int pulse)
    {
        var expected = _axisParameters[axis];
        // Convert a different live scale through raw pulses before converting micrometers to mm.
        return position * pulse / unit * expected.Unit / expected.Pulse / 1000;
    }

    private int GetAxis(MotionAxis axis)
    {
        return axis switch
        {
            MotionAxis.X => _axisX,
            MotionAxis.Y => _axisY!.Value,
            MotionAxis.Z => _axisZ!.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
    }

    private static bool IsBitSet(uint value, int bit)
    {
        return ((value >> bit) & 1) != 0;
    }

    private void PublishPosition()
    {
        var position = GetPosition();
        PublishPositionChanged(position.X, position.Y, position.Z);
    }
}
