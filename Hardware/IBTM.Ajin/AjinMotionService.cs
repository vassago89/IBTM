using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Ajin;

public class AjinMotionService(
    AjinController controller,
    AxisHardware axisX,
    AxisHardware? axisY,
    AxisHardware? axisZ,
    double millimetersPerPulse,
    MotionSettings settings,
    MachineOptions options,
    OperationCancellation operationCancellation,
    Func<double>? horizontalZ) : MotionService(
        settings,
        operationCancellation,
        hasY: axisY is not null,
        hasZ: axisZ is not null,
        horizontalZ: horizontalZ,
        xRange: (
            axisX.Minimum,
            axisX.Maximum),
        yRange: axisY is null ? null : (
            axisY.Minimum,
            axisY.Maximum),
        zRange: axisZ is null ? null : (
            axisZ.Minimum,
            axisZ.Maximum)), IMotionDiagnostics
{
    private const uint HomeSuccess = 0x01;
    private const uint HomeSearching = 0x02;
    private const uint HomeUnknown = 0xFF;
    private const int PositiveLimitBit = 0;
    private const int NegativeLimitBit = 1;
    private const int AlarmBit = 4;
    private const int InPositionBit = 5;
    private const int EmergencyBit = 6;
    private const int HomeSensorBit = 7;
    private const uint AccelerationInUnitsPerSecondSquared = 0;
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly int _axisX = axisX.Number;
    private readonly int? _axisY = axisY?.Number;
    private readonly int? _axisZ = axisZ?.Number;
    private readonly double _millimetersPerPulse = millimetersPerPulse;
    private readonly int[] _axes = new[] { axisX, axisY, axisZ }.OfType<AxisHardware>().Select(
        axis => axis.Number).ToArray();
    public override bool IsReady
    {
        get
        {
            return _axes.All(AxisParametersMatch);
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
        controller.Initialize();
        if (IsMoving)
        {
            throw new InvalidOperationException("Cannot configure axes while hardware reports motion.");
        }

        foreach (var axis in _axes)
        {
            if (AxisParametersMatch(axis))
            {
                continue;
            }

            // mm conversion belongs here, not in the .mot file's SDK scaling.
            AjinController.Check(
                CAXM.AxmMotSetMoveUnitPerPulse(axis, 1, 1),
                nameof(CAXM.AxmMotSetMoveUnitPerPulse));
            AjinController.Check(
                CAXM.AxmMotSetAccelUnit(axis, AccelerationInUnitsPerSecondSquared),
                nameof(CAXM.AxmMotSetAccelUnit));
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
        var velocityInUnits = ToUnits(velocity);
        var velocityX = velocityInUnits * distanceX / totalDistance;
        var velocityY = velocityInUnits * distanceY / totalDistance;
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
            cancellationToken);
    }

    protected override async Task MoveZToPositiveLimitCoreAsync(
        double velocity,
        CancellationToken cancellationToken)
    {
        var stopMode = 0U;
        var positiveLevel = 0U;
        var negativeLevel = 0U;
        AjinController.Check(
            CAXM.AxmSignalGetLimit(_axisZ!.Value, ref stopMode, ref positiveLevel, ref negativeLevel),
            nameof(CAXM.AxmSignalGetLimit));

        var velocityInUnits = ToUnits(velocity);
        var acceleration = velocityInUnits / Settings.AccelerationSeconds;

        await RunMoveAsync(
            () => CAXM.AxmMoveSignalSearch(
                _axisZ!.Value,
                velocityInUnits,
                acceleration,
                PositiveLimitBit,
                (int)positiveLevel,
                (int)stopMode),
            nameof(CAXM.AxmMoveSignalSearch),
            [_axisZ.Value],
            cancellationToken);

        if (!GetAxisState(MotionAxis.Z).PositiveLimit)
        {
            throw new InvalidOperationException("Z axis stopped before reaching its positive limit.");
        }
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
        return ReadDiagnosticState(axis);
    }

    // These getters never initialize the motion, change parameters, reset alarms or enable servos.
    // Disabled groups can therefore be monitored through the already-open AXL connection.
    public AxisState ReadDiagnosticState(MotionAxis axis)
    {
        var axisNumber = GetAxis(axis);
        var mechanical = 0U;
        var homeResult = 0U;
        var servoOn = 0U;
        var inMotion = 0U;
        AjinController.Check(
            CAXM.AxmStatusReadMechanical(axisNumber, ref mechanical),
            $"{nameof(CAXM.AxmStatusReadMechanical)} (axis={axisNumber})");
        AjinController.Check(
            CAXM.AxmHomeGetResult(axisNumber, ref homeResult),
            $"{nameof(CAXM.AxmHomeGetResult)} (axis={axisNumber})");
        AjinController.Check(
            CAXM.AxmSignalIsServoOn(axisNumber, ref servoOn),
            $"{nameof(CAXM.AxmSignalIsServoOn)} (axis={axisNumber})");
        AjinController.Check(
            CAXM.AxmStatusReadInMotion(axisNumber, ref inMotion),
            $"{nameof(CAXM.AxmStatusReadInMotion)} (axis={axisNumber})");

        return new AxisState(
            Homed: homeResult == HomeSuccess,
            ServoOn: servoOn != 0,
            Alarm: Bit(mechanical, AlarmBit),
            InPosition: Bit(mechanical, InPositionBit),
            Emergency: Bit(mechanical, EmergencyBit),
            HomeSensor: Bit(mechanical, HomeSensorBit),
            PositiveLimit: Bit(mechanical, PositiveLimitBit),
            NegativeLimit: Bit(mechanical, NegativeLimitBit),
            InMotion: inMotion != 0);
    }

    public double ReadDiagnosticPosition(MotionAxis axis)
    {
        return ReadPosition(GetAxis(axis));
    }

    protected override async Task<bool> HomeCoreAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var axisNumber = GetAxis(axis);
        EnsureAxisParameters(axisNumber);
        var velocityInUnits = ToUnits(velocity);

        var home = Settings.Home(axis);

        AjinController.Check(
            CAXM.AxmHomeSetResult(axisNumber, HomeUnknown),
            nameof(CAXM.AxmHomeSetResult));
        AjinController.Check(
            CAXM.AxmHomeSetVel(
                axisNumber,
                velocityInUnits,
                ToUnits(home.DetectionSpeed),
                ToUnits(home.ApproachSpeed),
                ToUnits(home.FineSpeed),
                velocityInUnits / home.SearchAccelerationSeconds,
                ToUnits(home.DetectionSpeed) / home.DetectionAccelerationSeconds),
            nameof(CAXM.AxmHomeSetVel));
        using var cancellationRegistration = cancellationToken.Register(
            () =>
            {
                CAXM.AxmMoveSStop(axisNumber);
                CAXM.AxmHomeSetResult(axisNumber, HomeUnknown);
            });
        Exception? failure = null;
        try
        {
            BeginMotion(axis != MotionAxis.Z);
            cancellationToken.ThrowIfCancellationRequested();
            AjinController.Check(CAXM.AxmHomeSetStart(axisNumber), nameof(CAXM.AxmHomeSetStart));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = 0U;
                AjinController.Check(
                    CAXM.AxmHomeGetResult(axisNumber, ref result),
                    nameof(CAXM.AxmHomeGetResult));
                PublishPosition();

                if (result == HomeSuccess)
                {
                    return true;
                }

                if (result != HomeSearching)
                {
                    CAXM.AxmMoveSStop(axisNumber);
                    return false;
                }

                await Task.Delay(StatusPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            CAXM.AxmMoveSStop(axisNumber);
            CAXM.AxmHomeSetResult(axisNumber, HomeUnknown);
            throw;
        }
        finally
        {
            await EndMotionAsync([axisNumber], axis != MotionAxis.Z, failure).ConfigureAwait(false);
        }
    }

    protected override async Task<bool> HomeHorizontalCoreAsync(
        double velocity,
        CancellationToken cancellationToken)
    {
        if (_axisY is null)
        {
            return await HomeCoreAsync(MotionAxis.X, velocity, cancellationToken);
        }

        using var homing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task<bool> HomeAxisAsync(MotionAxis axis)
        {
            var succeeded = false;
            try
            {
                succeeded = await HomeCoreAsync(axis, velocity, homing.Token);
                return succeeded;
            }
            finally
            {
                if (!succeeded)
                {
                    homing.Cancel();
                }
            }
        }

        try
        {
            var result = await Task.WhenAll(HomeAxisAsync(MotionAxis.X), HomeAxisAsync(MotionAxis.Y));
            return result[0] && result[1];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    protected override void ResetAlarm()
    {
        foreach (var axis in _axes)
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
            cancellationToken);
    }

    private async Task RunMoveAsync(
        Func<uint> move,
        string operation,
        int[] axes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var axis in axes)
        {
            EnsureAxisParameters(axis);
        }

        var horizontal = Array.Exists(axes, axis => axis != _axisZ);
        using var cancellationRegistration = cancellationToken.Register(StopAxes);
        Exception? failure = null;
        try
        {
            BeginMotion(horizontal);
            cancellationToken.ThrowIfCancellationRequested();
            AjinController.Check(move(), operation);
            await WaitForMoveAsync(axes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            failure = exception;
            StopAxes();
            throw;
        }
        catch (Exception exception)
        {
            var motionFailure = new MotionException(operation, exception);
            failure = motionFailure;
            StopAxes();
            throw motionFailure;
        }
        finally
        {
            await EndMotionAsync(axes, horizontal, failure).ConfigureAwait(false);
        }
    }

    private async Task EndMotionAsync(int[] axes, bool horizontal, Exception? failure)
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
            throw new MotionException(
                "Finish motion",
                failure is null ? cleanupFailure : new AggregateException(failure, cleanupFailure));
        }
    }

    protected async Task WaitForStopAsync(int[] axes)
    {
        var started = Stopwatch.GetTimestamp();
        // Stop is already requested. Cancellation must not skip its hardware acknowledgement.
        while (ReadMoveState(axes).Moving)
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= options.TimeoutMilliseconds)
                throw new TimeoutException(
                    $"Motion did not stop within {options.TimeoutMilliseconds} ms.");
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
                if (Stopwatch.GetElapsedTime(stoppedAt.Value).TotalMilliseconds >= options.TimeoutMilliseconds)
                    throw new TimeoutException(
                        $"In-position feedback was not received within {options.TimeoutMilliseconds} ms.");
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
            inPosition &= Bit(mechanical, InPositionBit);
            faulted |= Bit(mechanical, AlarmBit) || Bit(mechanical, EmergencyBit);
        }

        PublishPosition();
        return (moving, inPosition, faulted);
    }

    private void StopAxes()
    {
        foreach (var axis in _axes)
        {
            CAXM.AxmMoveSStop(axis);
        }
    }

    private double ReadPosition(int axis)
    {
        var position = 0.0;
        AjinController.Check(
            CAXM.AxmStatusGetActPos(axis, ref position),
            $"{nameof(CAXM.AxmStatusGetActPos)} (axis={axis})");
        var unit = 0.0;
        var pulse = 0;
        AjinController.Check(
            CAXM.AxmMotGetMoveUnitPerPulse(axis, ref unit, ref pulse),
            $"{nameof(CAXM.AxmMotGetMoveUnitPerPulse)} (axis={axis})");
        if (!double.IsFinite(unit) || unit <= 0 || pulse <= 0)
        {
            throw new System.IO.IOException(
                $"Invalid AJIN position scale (axis={axis}, unit={unit}, pulse={pulse}).");
        }

        return position * pulse / unit * _millimetersPerPulse;
    }

    private static bool AxisParametersMatch(int axis)
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
        return unit == 1
            && pulse == 1
            && accelerationUnit == AccelerationInUnitsPerSecondSquared;
    }

    private static void EnsureAxisParameters(int axis)
    {
        if (!AxisParametersMatch(axis))
        {
            throw new InvalidOperationException(
                $"AJIN axis {axis} unit settings changed. Initialize motion before issuing a move.");
        }
    }

    private double ToUnits(double millimeters)
    {
        return millimeters / _millimetersPerPulse;
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

    private static bool Bit(uint value, int bit)
    {
        return ((value >> bit) & 1) != 0;
    }

    private void PublishPosition()
    {
        var position = GetPosition();
        PublishPositionChanged(position.X, position.Y, position.Z);
    }

}
