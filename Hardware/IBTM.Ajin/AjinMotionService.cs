using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;
using Microsoft.Extensions.Logging;

namespace IBTM.Ajin;

public class AjinMotionService : MotionService, IMotionDiagnostics
{
    private const int PositiveLimitBit = 0;
    private const int NegativeLimitBit = 1;
    private const int AlarmBit = 4;
    private const int InPositionBit = 5;
    private const int EmergencyBit = 6;
    private const int HomeSensorBit = 7;
    private const uint AccelerationInUnitsPerSecondSquared = 0;
    private const uint AbsolutePositionMode = 0;
    private static readonly TimeSpan s_statusPollInterval;

    private readonly AjinController _controller;
    private readonly ILogger<AjinMotionService>? _log;
    private readonly MachineOptions _options;
    private readonly int _axisX;
    private readonly int? _axisY;
    private readonly int? _axisZ;
    private readonly Dictionary<int, AxisHardware> _axisParameters;

    static AjinMotionService()
    {
        s_statusPollInterval = TimeSpan.FromMilliseconds(10);
    }

    public AjinMotionService(
        AjinController controller,
        AxisHardware axisX,
        AxisHardware? axisY,
        AxisHardware? axisZ,
        MotionSettings settings,
        MachineOptions options,
        OperationCancellation operationCancellation,
        ILogger<AjinMotionService>? log = null)
        : base(
            settings,
            operationCancellation,
            hasY: axisY is not null,
            hasZ: axisZ is not null)
    {
        _controller = controller;
        _log = log;
        _options = options;
        _axisX = axisX.Number;
        _axisY = axisY?.Number;
        _axisZ = axisZ?.Number;
        _axisParameters = new[] { axisX, axisY, axisZ }
            .OfType<AxisHardware>()
            .ToDictionary(axis => axis.Number);
    }

    public override bool IsReady
    {
        get
        {
            foreach (var axis in _axisParameters.Keys)
            {
                var mechanical = 0U;
                AjinController.Check(
                    CAXM.AxmStatusReadMechanical(axis, ref mechanical),
                    $"{nameof(CAXM.AxmStatusReadMechanical)} (axis={axis})");
            }
            return true;
        }
    }

    public override bool IsMoving => Axes.Any(axis => GetAxisState(axis).InMotion);

    public override bool IsMovingHorizontal => Axes.Any(axis => axis != MotionAxis.Z && GetAxisState(axis).InMotion);

    public override void Initialize()
    {
        _controller.Initialize();
        foreach (var axis in _axisParameters.Keys)
        {
            var matches = DoAxisParametersMatch(axis, out var current);
            var scale = _axisParameters[axis];
            _log?.LogInformation(
                "AJIN axis {Axis} initialization: SDK Unit={Unit}, Pulse={Pulse}, AccelUnit={AccelUnit}; configured Unit={ConfiguredUnit}, Pulse={ConfiguredPulse}, AccelUnit={ConfiguredAccelUnit}. Apply={Apply}.",
                axis, current.Unit, current.Pulse, current.AccelerationUnit,
                scale.MoveUnit, scale.MovePulse, AccelerationInUnitsPerSecondSquared, !matches);
            if (matches)
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

            AjinController.Check(
                CAXM.AxmMotSetMoveUnitPerPulse(axis, scale.MoveUnit, scale.MovePulse),
                $"{nameof(CAXM.AxmMotSetMoveUnitPerPulse)} (axis={axis})");
            
            AjinController.Check(
                CAXM.AxmMotSetAccelUnit(axis, AccelerationInUnitsPerSecondSquared),
                $"{nameof(CAXM.AxmMotSetAccelUnit)} (axis={axis})");
            if (!DoAxisParametersMatch(axis, out current))
            {
                throw new MotionInterlockException(
                    $"AJIN axis {axis} initialization did not retain motion parameters: "
                    + $"SDK Unit={current.Unit}, Pulse={current.Pulse}, AccelUnit={current.AccelerationUnit}; "
                    + $"configured Unit={scale.MoveUnit}, Pulse={scale.MovePulse}, "
                    + $"AccelUnit={AccelerationInUnitsPerSecondSquared}.");
            }
            _log?.LogInformation("AJIN axis {Axis} unit settings applied and read back successfully.", axis);
        }

        // Communication readiness is independent of servo power and axis alarms.
        // Servo ON belongs to an explicit operator command (or the existing RESET flow).
        PublishPosition();
        PublishStateChanged();
    }

    protected override async Task MoveXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        var axisYNumber = _axisY!.Value;
        var position = GetPosition();
        var distanceX = Math.Abs(x - position.X);
        var distanceY = Math.Abs(y - position.Y);

        switch ((distanceX, distanceY))
        {
            case (0, 0):
                return;
            case (0, _):
                await MoveAsync(MotionAxis.Y, y, velocity, cancellationToken).ConfigureAwait(false);
                return;
            case (_, 0):
                await MoveAsync(MotionAxis.X, x, velocity, cancellationToken).ConfigureAwait(false);
                return;
        }

        ValidatePositive(Settings.AccelerationSeconds, nameof(Settings.AccelerationSeconds));
        ValidatePositive(Settings.DecelerationSeconds, nameof(Settings.DecelerationSeconds));
        var totalDistance = distanceX + distanceY;
        var velocityX = ToUnits(velocity * distanceX / totalDistance);
        var velocityY = ToUnits(velocity * distanceY / totalDistance);
        var axes = new[] { _axisX, axisYNumber };

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            BeginMotion(horizontal: true);
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var axis in axes)
                    SetAbsolutePositionMode(axis);
                cancellationToken.ThrowIfCancellationRequested();
                AjinController.Check(
                    CAXM.AxmMoveStartMultiPos(
                        axes.Length,
                        axes,
                        [ToUnits(x), ToUnits(y)],
                        [velocityX, velocityY],
                        [velocityX / Settings.AccelerationSeconds, velocityY / Settings.AccelerationSeconds],
                        [velocityX / Settings.DecelerationSeconds, velocityY / Settings.DecelerationSeconds]),
                    nameof(CAXM.AxmMoveStartMultiPos));
            }).ConfigureAwait(false);
            await WaitForMoveAsync(axes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await StopAfterFailureAsync(axes, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            EndMotion(horizontal: true);
        }
    }

    public override async Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        ValidateJog(axis, velocity);
        ValidatePositive(Settings.AccelerationSeconds, nameof(Settings.AccelerationSeconds));
        ValidatePositive(Settings.DecelerationSeconds, nameof(Settings.DecelerationSeconds));
        var axisNumber = GetAxis(axis);
        var velocityInUnits = ToUnits(velocity);
        var acceleration = Math.Abs(velocityInUnits) / Settings.AccelerationSeconds;
        var deceleration = Math.Abs(velocityInUnits) / Settings.DecelerationSeconds;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            BeginMotion(axis != MotionAxis.Z, adjustment: true);
            AjinController.Check(
                CAXM.AxmMoveVel(axisNumber, velocityInUnits, acceleration, deceleration),
                nameof(CAXM.AxmMoveVel));
            await WaitForMoveAsync([axisNumber], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await StopAfterFailureAsync([axisNumber], exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            EndMotion(axis != MotionAxis.Z);
        }
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
            Homed: homeResult == (uint)AXT_MOTION_HOME_RESULT.HOME_SUCCESS,
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

    protected override async Task<bool> HomeAxesAsync(
        MotionAxis[] axes,
        double velocity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var axisNumbers = axes.Select(GetAxis).ToArray();
        var home = Settings.Home(axes[0]);
        ValidatePositive(home.DetectionSpeed, nameof(home.DetectionSpeed));
        ValidatePositive(home.ApproachSpeed, nameof(home.ApproachSpeed));
        ValidatePositive(home.FineSpeed, nameof(home.FineSpeed));
        ValidatePositive(home.SearchAccelerationSeconds, nameof(home.SearchAccelerationSeconds));
        ValidatePositive(home.DetectionAccelerationSeconds, nameof(home.DetectionAccelerationSeconds));
        var horizontal = axes.Any(axis => axis != MotionAxis.Z);
        try
        {
            BeginMotion(horizontal);
            velocity *= 1000;
            foreach (var axis in axes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var axisNumber = GetAxis(axis);
                var direction = 0;
                var signal = 4U;
                var zPhase = 0U;
                var clearTime = 1000d;
                var offset = 0d;

                AjinController.Check(
                    CAXM.AxmHomeGetMethod(
                        axisNumber, ref direction, ref signal, ref zPhase, ref clearTime, ref offset),
                    $"{nameof(CAXM.AxmHomeGetMethod)} (axis={axisNumber})");

                direction = _axisParameters[axisNumber].HomeDirection switch
                {
                    HomeDirection.Negative => 0,
                    HomeDirection.Positive => 1,
                    var value => throw new InvalidOperationException($"Invalid home direction for axis {axisNumber}: {value}."),
                };
                
                if (CAXM.AxmStatusSetActPos(axisNumber, 0)
                        != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS
                    || CAXM.AxmHomeSetResult(axisNumber, (uint)AXT_MOTION_HOME_RESULT.HOME_ERR_UNKNOWN)
                        != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS
                    || CAXM.AxmHomeSetMethod(axisNumber, direction, signal, zPhase, clearTime, offset)
                        != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS
                    || CAXM.AxmHomeSetVel(
                        axisNumber, velocity,
                        ToUnits(home.DetectionSpeed), ToUnits(home.ApproachSpeed), ToUnits(home.FineSpeed),
                        velocity / home.SearchAccelerationSeconds,
                        ToUnits(home.DetectionSpeed) / home.DetectionAccelerationSeconds)
                        != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS
                    || CAXM.AxmHomeSetStart(axisNumber) != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
                {
                    StopAxes(axisNumbers);
                    await WaitForStopAsync(axisNumbers).ConfigureAwait(false);
                    return false;
                }
            }

            return await Task.Run(async () =>
            {
                await Task.Delay(100);

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var allHomed = true;
                    foreach (var axisNumber in axisNumbers)
                    {
                        uint result = 0;
                        AjinController.Check(
                            CAXM.AxmHomeGetResult(axisNumber, ref result),
                            $"{nameof(CAXM.AxmHomeGetResult)} (axis={axisNumber})");
                        if (result == (uint)AXT_MOTION_HOME_RESULT.HOME_SUCCESS)
                            continue;

                        if (result != (uint)AXT_MOTION_HOME_RESULT.HOME_SEARCHING)
                        {
                            StopAxes(axisNumbers);
                            await WaitForStopAsync(axisNumbers).ConfigureAwait(false);
                            return false;
                        }
                        allHomed = false;
                    }
                    if (allHomed)
                    {
                        await WaitForStopAsync(axisNumbers).ConfigureAwait(false);
                        return true;
                    }
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await StopAfterFailureAsync(axisNumbers, exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            EndMotion(horizontal);
        }
    }

    protected override async Task ResetAlarmAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            foreach (var axis in _axisParameters.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AjinController.Check(
                    CAXM.AxmSignalServoAlarmReset(axis, 1),
                    $"{nameof(CAXM.AxmSignalServoAlarmReset)} (axis={axis}, on=1)");
            }
            // Keep the reference equipment's one-second reset pulse.
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            var failures = new List<Exception>();
            foreach (var axis in _axisParameters.Keys)
            {
                try
                {
                    // Release every reset output even after cancellation or another axis fails.
                    AjinController.Check(
                        CAXM.AxmSignalServoAlarmReset(axis, 0),
                        $"{nameof(CAXM.AxmSignalServoAlarmReset)} (axis={axis}, on=0)");
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            if (failures.Count > 0)
            {
                if (failure is not null)
                    failures.Insert(0, failure);
                throw new AggregateException("Failed to release servo alarm reset outputs.", failures);
            }
            PublishStateChanged();
        }
    }

    protected override async Task MoveAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePositive(Settings.AccelerationSeconds, nameof(Settings.AccelerationSeconds));
        ValidatePositive(Settings.DecelerationSeconds, nameof(Settings.DecelerationSeconds));
        var axisNumber = GetAxis(axis);
        var velocityInUnits = velocity * 1000;
        try
        {
            BeginMotion(axis != MotionAxis.Z);
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetAbsolutePositionMode(axisNumber);
                cancellationToken.ThrowIfCancellationRequested();
                AjinController.Check(
                    CAXM.AxmMoveStartPos(
                        axisNumber, position * 1000, velocityInUnits,
                        velocityInUnits / Settings.AccelerationSeconds,
                        velocityInUnits / Settings.DecelerationSeconds),
                    $"{nameof(CAXM.AxmMoveStartPos)} (axis={axisNumber})");
            }).ConfigureAwait(false);
            await WaitForMoveAsync([axisNumber], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await StopAfterFailureAsync([axisNumber], exception).ConfigureAwait(false);
            throw;
        }
        finally
        {
            EndMotion(axis != MotionAxis.Z);
        }
    }

    private static void SetAbsolutePositionMode(int axis)
    {
        AjinController.Check(
            CAXM.AxmMotSetAbsRelMode(axis, AbsolutePositionMode),
            $"{nameof(CAXM.AxmMotSetAbsRelMode)} (axis={axis})");
        var mode = uint.MaxValue;
        AjinController.Check(
            CAXM.AxmMotGetAbsRelMode(axis, ref mode),
            $"{nameof(CAXM.AxmMotGetAbsRelMode)} (axis={axis})");
        if (mode != AbsolutePositionMode)
        {
            throw new MotionInterlockException(
                $"AJIN axis {axis} is not in absolute positioning mode (mode={mode}).");
        }
    }

    private async Task StopAfterFailureAsync(int[] axes, Exception failure)
    {
        List<Exception> failures = [failure];
        try
        {
            StopAxes(axes);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await WaitForStopAsync(axes).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count > 1)
            throw new MotionException("Stop motion", new AggregateException(failures));
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
            await Task.Delay(s_statusPollInterval).ConfigureAwait(false);
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
                throw new MotionInterlockException("Motion stopped by an axis fault.");
            if (!moving && inPosition)
                return;

            if (moving)
                stoppedAt = null;
            else
            {
                stoppedAt ??= Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(stoppedAt.Value).TotalMilliseconds >= _options.TimeoutMilliseconds)
                    throw new MotionException("Move", new TimeoutException(
                        $"In-position feedback was not received within {_options.TimeoutMilliseconds} ms."));
            }

            await Task.Delay(s_statusPollInterval, cancellationToken).ConfigureAwait(false);
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

        // The SDK has already applied Unit/Pulse. The application's motion unit is micrometers.
        var millimeters = position / 1000;
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

    private bool DoAxisParametersMatch(
        int axis,
        out (double Unit, int Pulse, uint AccelerationUnit) current)
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
        current = (unit, pulse, accelerationUnit);
        var expected = _axisParameters[axis];
        return unit == expected.MoveUnit
            && pulse == expected.MovePulse
            && accelerationUnit == AccelerationInUnitsPerSecondSquared;
    }

    private static double ToUnits(double millimeters)
    {
        return millimeters * 1000;
    }

    private int GetAxis(MotionAxis axis)
    {
        switch (axis)
        {
            case MotionAxis.X:
                return _axisX;
            case MotionAxis.Y:
                return _axisY!.Value;
            case MotionAxis.Z:
                return _axisZ!.Value;
            default:
                throw new ArgumentOutOfRangeException(nameof(axis));
        }
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
