using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Ajin;

public sealed class AjinMotionService(
    AjinController controller,
    AxisHardware axisX,
    AxisHardware? axisY,
    AxisHardware? axisZ,
    double millimetersPerPulse,
    MotionSettings settings,
    OperationCancellation operationCancellation,
    Func<double>? horizontalZ) : MotionService(
        settings,
        operationCancellation,
        hasY: axisY is not null,
        hasZ: axisZ is not null,
        horizontalZ: horizontalZ,
        xRange: (axisX.Minimum, axisX.Maximum),
        yRange: axisY is null
            ? null
            : (axisY.Minimum, axisY.Maximum),
        zRange: axisZ is null
            ? null
            : (axisZ.Minimum, axisZ.Maximum))
{
    private const uint HomeSuccess = 0x01;
    private const uint HomeSearching = 0x02;
    private const uint HomeUnknown = 0xFF;
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly int _axisX = axisX.Number;
    private readonly int? _axisY = axisY?.Number;
    private readonly int? _axisZ = axisZ?.Number;
    private readonly int _directionX = (int)axisX.Direction;
    private readonly int _directionY = axisY is null
        ? 0
        : (int)axisY.Direction;
    private readonly int _directionZ = axisZ is null
        ? 0
        : (int)axisZ.Direction;
    private readonly double _millimetersPerPulse = millimetersPerPulse;
    private readonly int[] _axes = GetAxes(axisX, axisY, axisZ);
    public override void Initialize()
    {
        controller.Initialize();
        foreach (var axis in _axes)
        {
            SetServo(axis, true);
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
        CancellationToken cancellationToken) =>
        MoveAxisAsync(
            _axisX,
            _directionX,
            x,
            velocity,
            cancellationToken);

    protected override Task MoveYCoreAsync(
        double y,
        double velocity,
        CancellationToken cancellationToken) =>
        MoveAxisAsync(
            _axisY!.Value,
            _directionY,
            y,
            velocity,
            cancellationToken);

    protected override Task MoveZCoreAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveAxisAsync(
            _axisZ!.Value,
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
                _axisZ!.Value,
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
                _axisZ!.Value,
                velocityInUnits,
                acceleration,
                detectSignal,
                (int)signalEdge,
                (int)stopMode),
            nameof(AjinNative.AxmMoveSignalSearch),
            [_axisZ.Value],
            cancellationToken);

        if (!GetAxisState(MotionAxis.Z).PositiveLimit)
        {
            throw new InvalidOperationException(
                "Z axis stopped before reaching its positive limit.");
        }
    }

    protected override void JogXCore(
        double velocity,
        CancellationToken cancellationToken) =>
        Jog(_axisX, velocity * _directionX, cancellationToken);

    protected override void JogYCore(
        double velocity,
        CancellationToken cancellationToken) =>
        Jog(_axisY!.Value, velocity * _directionY, cancellationToken);

    protected override void JogZCore(
        double velocity,
        CancellationToken cancellationToken) =>
        Jog(_axisZ!.Value, velocity * _directionZ, cancellationToken);

    public override void SetServo(MotionAxis axis, bool on) =>
        SetServo(GetAxis(axis), on);

    public override (double X, double Y, double Z) GetPosition() =>
        (
            ReadPosition(_axisX) * _directionX,
            _axisY is null ? 0 : ReadPosition(_axisY.Value) * _directionY,
            _axisZ is null ? 0 : ReadPosition(_axisZ.Value) * _directionZ);

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
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            AjinNative.AxmMoveSStop(axisNumber);
            AjinNative.AxmHomeSetResult(axisNumber, HomeUnknown);
        });
        BeginMotion();

        try
        {
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

                await Task.Delay(StatusPollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            EndMotion();
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
        using var cancellationRegistration =
            cancellationToken.Register(StopAxes);
        BeginMotion();
        try
        {
            try
            {
                AjinController.Check(move(), operation);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var moving = false;
                    var inPosition = true;
                    var faulted = false;
                    foreach (var axis in axes)
                    {
                        var inMotion = 0U;
                        var mechanical = 0U;
                        AjinController.Check(
                            AjinNative.AxmStatusReadInMotion(axis, ref inMotion),
                            nameof(AjinNative.AxmStatusReadInMotion));
                        AjinController.Check(
                            AjinNative.AxmStatusReadMechanical(
                                axis,
                                ref mechanical),
                            nameof(AjinNative.AxmStatusReadMechanical));
                        moving |= inMotion != 0;
                        inPosition &= Bit(mechanical, 5);
                        faulted |= Bit(mechanical, 4)
                                   || Bit(mechanical, 6);
                    }

                    PublishPosition();
                    if (faulted)
                    {
                        throw new InvalidOperationException(
                            "Motion stopped by an axis fault.");
                    }

                    if (!moving)
                    {
                        if (inPosition)
                        {
                            return;
                        }

                        throw new InvalidOperationException(
                            "Motion stopped before reaching its target.");
                    }

                    await Task.Delay(StatusPollInterval, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                StopAxes();
                throw;
            }
            catch (Exception exception)
            {
                StopAxes();
                throw new MotionException(operation, exception);
            }
        }
        finally
        {
            EndMotion();
            PublishPosition();
        }
    }

    private void Jog(
        int axis,
        double velocity,
        CancellationToken cancellationToken)
    {
        var monitor = LinkOperation(cancellationToken);
        monitor.Token.ThrowIfCancellationRequested();
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
        _ = MonitorJogPositionAsync(monitor);
    }

    private async Task MonitorJogPositionAsync(
        CancellationTokenSource monitor)
    {
        using var cancellationRegistration =
            monitor.Token.Register(StopAxes);
        try
        {
            while (true)
            {
                PublishPosition();
                await Task.Delay(StatusPollInterval, monitor.Token)
                    .ConfigureAwait(false);
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

    private void StopAxes()
    {
        foreach (var axis in _axes)
        {
            AjinNative.AxmMoveSStop(axis);
        }
    }

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
        MotionAxis.Z => _axisZ!.Value,
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

    private static int[] GetAxes(
        AxisHardware axisX,
        AxisHardware? axisY,
        AxisHardware? axisZ)
    {
        var axes = new System.Collections.Generic.List<int>
        {
            axisX.Number,
        };
        if (axisY is { } y)
        {
            axes.Add(y.Number);
        }

        if (axisZ is { } z)
        {
            axes.Add(z.Number);
        }

        return [.. axes];
    }

    private void PublishPosition()
    {
        var position = GetPosition();
        PublishPositionChanged(position.X, position.Y, position.Z);
    }

}
