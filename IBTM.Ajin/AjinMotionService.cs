using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Ajin;

public sealed class AjinMotionService(
    AjinController controller,
    HardwareMap hardware,
    MachineAxis axisX,
    MachineAxis? axisY,
    MachineAxis axisZ,
    MotionSettings settings) : MotionService(
        settings,
        axisY is not null,
        (hardware.AxisMinimums[axisX], hardware.AxisMaximums[axisX]),
        axisY is null
            ? null
            : (hardware.AxisMinimums[axisY.Value], hardware.AxisMaximums[axisY.Value]),
        (hardware.AxisMinimums[axisZ], hardware.AxisMaximums[axisZ]))
{
    private const uint HomeSuccess = 0x01;
    private const uint HomeSearching = 0x02;
    private const uint HomeUnknown = 0xFF;
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly int _axisX = hardware.Axes[axisX];
    private readonly int? _axisY = axisY is null
        ? null
        : hardware.Axes[axisY.Value];
    private readonly int _axisZ = hardware.Axes[axisZ];
    private readonly int _directionX = (int)hardware.AxisDirections[axisX];
    private readonly int _directionY = axisY is null
        ? 0
        : (int)hardware.AxisDirections[axisY.Value];
    private readonly int _directionZ = (int)hardware.AxisDirections[axisZ];
    private readonly double _millimetersPerPulse = hardware.MillimetersPerPulse;
    private readonly int[] _axes = axisY is null
        ? [hardware.Axes[axisX], hardware.Axes[axisZ]]
        : [hardware.Axes[axisX], hardware.Axes[axisY.Value], hardware.Axes[axisZ]];
    private CancellationTokenSource? _jogMonitor;

    public override void Initialize()
    {
        controller.Initialize();
        foreach (var axis in _axes)
        {
            ServoOn(axis);
        }

        PublishPosition();
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
            return MoveAxisAsync(
                axisYNumber,
                _directionY,
                y,
                velocity,
                cancellationToken);
        }

        if (distanceY == 0)
        {
            return MoveAxisAsync(
                _axisX,
                _directionX,
                x,
                velocity,
                cancellationToken);
        }

        var totalDistance = Math.Sqrt(
            distanceX * distanceX
            + distanceY * distanceY);
        var velocityInUnits = ToUnits(velocity);
        var velocityX = velocityInUnits * distanceX / totalDistance;
        var velocityY = velocityInUnits * distanceY / totalDistance;
        var accelerationMultiplier = controller.Settings.AccelerationMultiplier;

        return RunMoveAsync(
            () => AjinNative.AxmMoveMultiPos(
                2,
                [_axisX, axisYNumber],
                [ToUnits(x * _directionX), ToUnits(y * _directionY)],
                [velocityX, velocityY],
                [velocityX * accelerationMultiplier, velocityY * accelerationMultiplier],
                [velocityX * accelerationMultiplier, velocityY * accelerationMultiplier]),
            nameof(AjinNative.AxmMoveMultiPos),
            [_axisX, axisYNumber],
            cancellationToken);
    }

    protected override Task MoveXCoreAsync(
        double x,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveAxisAsync(
            _axisX,
            _directionX,
            x,
            velocity,
            cancellationToken);

    protected override Task MoveZCoreAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveAxisAsync(
            _axisZ,
            _directionZ,
            z,
            velocity,
            cancellationToken);

    protected override async Task MoveZToPositiveLimitCoreAsync(
        double velocity,
        CancellationToken cancellationToken)
    {
        var stopMode = 0U;
        var positiveLevel = 0U;
        var negativeLevel = 0U;
        AjinController.Check(
            AjinNative.AxmSignalGetLimit(
                _axisZ,
                ref stopMode,
                ref positiveLevel,
                ref negativeLevel),
            nameof(AjinNative.AxmSignalGetLimit));

        var detectSignal = _directionZ > 0 ? 0 : 1;
        var signalEdge = _directionZ > 0
            ? positiveLevel
            : negativeLevel;
        var velocityInUnits = ToUnits(velocity) * _directionZ;
        var acceleration = Math.Abs(velocityInUnits)
                           * controller.Settings.AccelerationMultiplier;

        await RunMoveAsync(
            () => AjinNative.AxmMoveSignalSearch(
                _axisZ,
                velocityInUnits,
                acceleration,
                detectSignal,
                (int)signalEdge,
                (int)stopMode),
            nameof(AjinNative.AxmMoveSignalSearch),
            [_axisZ],
            cancellationToken);

        if (!GetAxisState(MotionAxis.Z).PositiveLimit)
        {
            throw new InvalidOperationException(
                "Z axis stopped before reaching its positive limit.");
        }
    }

    protected override void JogXCore(double velocity) =>
        Jog(_axisX, velocity * _directionX);

    protected override void JogYCore(double velocity) =>
        Jog(_axisY!.Value, velocity * _directionY);

    protected override void JogZCore(double velocity) =>
        Jog(_axisZ, velocity * _directionZ);

    public override void Stop()
    {
        StopAxes(emergency: false);
        StopJogMonitor();
        PublishPosition();
    }

    public override void EmergencyStop()
    {
        StopAxes(emergency: true);
        StopJogMonitor();
        PublishPosition();
    }

    public override void SetServo(MotionAxis axis, bool on) =>
        SetServo(GetAxis(axis), on);

    public override (double X, double Y, double Z) GetPosition() =>
        (
            ReadPosition(_axisX) * _directionX,
            _axisY is null ? 0 : ReadPosition(_axisY.Value) * _directionY,
            ReadPosition(_axisZ) * _directionZ);

    public override AxisState GetAxisState(MotionAxis axis)
    {
        var axisNumber = GetAxis(axis);
        var mechanical = 0U;
        var homeResult = 0U;
        var servoOn = 0U;
        var direction = GetDirection(axis);
        AjinController.Check(
            AjinNative.AxmStatusReadMechanical(axisNumber, ref mechanical),
            nameof(AjinNative.AxmStatusReadMechanical));
        AjinController.Check(
            AjinNative.AxmHomeGetResult(axisNumber, ref homeResult),
            nameof(AjinNative.AxmHomeGetResult));
        AjinController.Check(
            AjinNative.AxmSignalIsServoOn(axisNumber, ref servoOn),
            nameof(AjinNative.AxmSignalIsServoOn));

        return new AxisState(
            Homed: homeResult == HomeSuccess,
            ServoOn: servoOn != 0,
            Alarm: Bit(mechanical, 4),
            InPosition: Bit(mechanical, 5),
            Emergency: Bit(mechanical, 6),
            HomeSensor: Bit(mechanical, 7),
            PositiveLimit: Bit(mechanical, direction > 0 ? 0 : 1),
            NegativeLimit: Bit(mechanical, direction > 0 ? 1 : 0));
    }

    protected override async Task<bool> HomeCoreAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var axisNumber = GetAxis(axis);
        var velocityInUnits = ToUnits(velocity);
        var started = false;
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            AjinNative.AxmMoveSStop(axisNumber);
            AjinNative.AxmHomeSetResult(axisNumber, HomeUnknown);
        });

        try
        {
            AjinController.Check(
                AjinNative.AxmHomeSetResult(axisNumber, HomeUnknown),
                nameof(AjinNative.AxmHomeSetResult));
            AjinController.Check(
                AjinNative.AxmHomeSetVel(
                    axisNumber,
                    velocityInUnits,
                    velocityInUnits / 5,
                    velocityInUnits / 10,
                    velocityInUnits / 100,
                    velocityInUnits,
                    velocityInUnits / 10),
                nameof(AjinNative.AxmHomeSetVel));
            AjinController.Check(
                AjinNative.AxmHomeSetStart(axisNumber),
                nameof(AjinNative.AxmHomeSetStart));
            BeginMotion();
            started = true;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = 0U;
                AjinController.Check(
                    AjinNative.AxmHomeGetResult(axisNumber, ref result),
                    nameof(AjinNative.AxmHomeGetResult));

                if (result == HomeSuccess)
                {
                    return true;
                }

                if (result != HomeSearching)
                {
                    return false;
                }

                await Task.Delay(StatusPollInterval, cancellationToken);
            }
        }
        finally
        {
            if (started)
            {
                EndMotion();
            }

            PublishPosition();
        }
    }

    protected override async Task<bool> HomeHorizontalCoreAsync(
        double velocity,
        CancellationToken cancellationToken)
    {
        if (_axisY is null)
        {
            return await HomeCoreAsync(
                MotionAxis.X,
                velocity,
                cancellationToken);
        }

        var result = await Task.WhenAll(
            HomeCoreAsync(MotionAxis.X, velocity, cancellationToken),
            HomeCoreAsync(MotionAxis.Y, velocity, cancellationToken));
        return result[0] && result[1];
    }

    public override void ResetAlarm()
    {
        foreach (var axis in _axes)
        {
            AjinController.Check(
                AjinNative.AxmSignalServoAlarmReset(axis, 1),
                nameof(AjinNative.AxmSignalServoAlarmReset));
        }
    }

    private Task MoveAxisAsync(
        int axis,
        int direction,
        double position,
        double velocity,
        CancellationToken cancellationToken)
    {
        var velocityInUnits = ToUnits(velocity);
        var acceleration = velocityInUnits * controller.Settings.AccelerationMultiplier;

        return RunMoveAsync(
            () => AjinNative.AxmMovePos(
                axis,
                ToUnits(position * direction),
                velocityInUnits,
                acceleration,
                acceleration),
            nameof(AjinNative.AxmMovePos),
            [axis],
            cancellationToken);
    }

    private async Task RunMoveAsync(
        Func<uint> move,
        string operation,
        int[] axes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = false;
        using var cancellationRegistration =
            cancellationToken.Register(() => StopAxes(emergency: false));

        try
        {
            AjinController.Check(move(), operation);
            BeginMotion();
            started = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var moving = false;
                foreach (var axis in axes)
                {
                    var inMotion = 0U;
                    AjinController.Check(
                        AjinNative.AxmStatusReadInMotion(axis, ref inMotion),
                        nameof(AjinNative.AxmStatusReadInMotion));
                    moving |= inMotion != 0;
                }

                PublishPosition();
                if (!moving)
                {
                    return;
                }

                await Task.Delay(StatusPollInterval, cancellationToken);
            }
        }
        finally
        {
            if (started)
            {
                EndMotion();
            }

            PublishPosition();
        }
    }

    private void Jog(int axis, double velocity)
    {
        var velocityInUnits = ToUnits(velocity);
        var acceleration = Math.Abs(velocityInUnits)
                           * controller.Settings.AccelerationMultiplier;
        AjinController.Check(
            AjinNative.AxmMoveVel(
                axis,
                velocityInUnits,
                acceleration,
                acceleration),
            nameof(AjinNative.AxmMoveVel));
        BeginMotion();
        _jogMonitor = new CancellationTokenSource();
        _ = MonitorJogPositionAsync(_jogMonitor);
    }

    private async Task MonitorJogPositionAsync(
        CancellationTokenSource monitor)
    {
        try
        {
            while (true)
            {
                PublishPosition();
                await Task.Delay(StatusPollInterval, monitor.Token);
            }
        }
        catch (OperationCanceledException) when (monitor.IsCancellationRequested)
        {
        }
        finally
        {
            EndMotion();
            monitor.Dispose();
        }
    }

    private void StopJogMonitor()
    {
        _jogMonitor?.Cancel();
        _jogMonitor = null;
    }

    private void StopAxes(bool emergency)
    {
        foreach (var axis in _axes)
        {
            var result = emergency
                ? AjinNative.AxmMoveEStop(axis)
                : AjinNative.AxmMoveSStop(axis);
            AjinController.Check(
                result,
                emergency
                    ? nameof(AjinNative.AxmMoveEStop)
                    : nameof(AjinNative.AxmMoveSStop));
        }
    }

    private void ServoOn(int axis) => SetServo(axis, true);

    private void SetServo(int axis, bool on) =>
        AjinController.Check(
            AjinNative.AxmSignalServoOn(axis, on ? 1U : 0U),
            nameof(AjinNative.AxmSignalServoOn));

    private double ReadPosition(int axis)
    {
        var position = 0.0;
        AjinController.Check(
            AjinNative.AxmStatusGetActPos(axis, ref position),
            nameof(AjinNative.AxmStatusGetActPos));
        return position * _millimetersPerPulse;
    }

    private double ToUnits(double millimeters) =>
        millimeters / _millimetersPerPulse;

    private int GetAxis(MotionAxis axis) => axis switch
    {
        MotionAxis.X => _axisX,
        MotionAxis.Y => _axisY!.Value,
        MotionAxis.Z => _axisZ,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    private int GetDirection(MotionAxis axis) => axis switch
    {
        MotionAxis.X => _directionX,
        MotionAxis.Y => _directionY,
        MotionAxis.Z => _directionZ,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    private static bool Bit(uint value, int bit) =>
        ((value >> bit) & 1) != 0;

    private void PublishPosition()
    {
        var position = GetPosition();
        PublishPositionChanged(position.X, position.Y, position.Z);
    }

}
