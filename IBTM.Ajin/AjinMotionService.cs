using System;
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
    private const int PositiveLimitBit = 0;
    private const int NegativeLimitBit = 1;
    private const int AlarmBit = 4;
    private const int InPositionBit = 5;
    private const int EmergencyBit = 6;
    private const int HomeSensorBit = 7;
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly int _axisX = axisX.Number;
    private readonly int? _axisY = axisY?.Number;
    private readonly int? _axisZ = axisZ?.Number;
    private readonly double _millimetersPerPulse = millimetersPerPulse;
    private readonly int[] _axes = GetAxes(axisX, axisY, axisZ);
    private bool _initialized;

    public override bool IsReady => _initialized;

    public override void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        controller.Initialize();
        foreach (var axis in _axes)
        {
            SetServo(axis, true);
        }

        _initialized = true;
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
                y,
                velocity,
                cancellationToken);
        }

        if (distanceY == 0)
        {
            return MoveAxisAsync(
                _axisX,
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
        var axes = new[] { _axisX, axisYNumber };

        return RunMoveAsync(
            () => AjinNative.AxmMoveMultiPos(
                axes.Length,
                axes,
                [ToUnits(x), ToUnits(y)],
                [velocityX, velocityY],
                [velocityX * accelerationMultiplier, velocityY * accelerationMultiplier],
                [velocityX * accelerationMultiplier, velocityY * accelerationMultiplier]),
            nameof(AjinNative.AxmMoveMultiPos),
            axes,
            cancellationToken);
    }

    protected override Task MoveXCoreAsync(
        double x,
        double velocity,
        CancellationToken cancellationToken) =>
        MoveAxisAsync(
            _axisX,
            x,
            velocity,
            cancellationToken);

    protected override Task MoveYCoreAsync(
        double y,
        double velocity,
        CancellationToken cancellationToken) =>
        MoveAxisAsync(
            _axisY!.Value,
            y,
            velocity,
            cancellationToken);

    protected override Task MoveZCoreAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveAxisAsync(
            _axisZ!.Value,
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

        var velocityInUnits = ToUnits(velocity);
        var acceleration = velocityInUnits
                           * controller.Settings.AccelerationMultiplier;

        await RunMoveAsync(
            () => AjinNative.AxmMoveSignalSearch(
                _axisZ!.Value,
                velocityInUnits,
                acceleration,
                PositiveLimitBit,
                (int)positiveLevel,
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
        Jog(_axisX, velocity, cancellationToken);

    protected override void JogYCore(
        double velocity,
        CancellationToken cancellationToken) =>
        Jog(_axisY!.Value, velocity, cancellationToken);

    protected override void JogZCore(
        double velocity,
        CancellationToken cancellationToken) =>
        Jog(_axisZ!.Value, velocity, cancellationToken);

    public override void SetServo(MotionAxis axis, bool on)
    {
        SetServo(GetAxis(axis), on);
        PublishStateChanged();
    }

    public override (double X, double Y, double Z) GetPosition() =>
        !_initialized
            ? default
            : (
                ReadPosition(_axisX),
                _axisY is null ? 0 : ReadPosition(_axisY.Value),
                _axisZ is null ? 0 : ReadPosition(_axisZ.Value));

    public override AxisState GetAxisState(MotionAxis axis)
    {
        if (!_initialized)
        {
            return new AxisState(
                Homed: false,
                ServoOn: false,
                Alarm: true,
                InPosition: false,
                Emergency: false,
                HomeSensor: false,
                PositiveLimit: false,
                NegativeLimit: false);
        }

        var axisNumber = GetAxis(axis);
        var mechanical = 0U;
        var homeResult = 0U;
        var servoOn = 0U;
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
            Alarm: Bit(mechanical, AlarmBit),
            InPosition: Bit(mechanical, InPositionBit),
            Emergency: Bit(mechanical, EmergencyBit),
            HomeSensor: Bit(mechanical, HomeSensorBit),
            PositiveLimit: Bit(mechanical, PositiveLimitBit),
            NegativeLimit: Bit(mechanical, NegativeLimitBit));
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
                velocityInUnits
                * controller.Settings.HomeSecondVelocityRatio,
                velocityInUnits
                * controller.Settings.HomeThirdVelocityRatio,
                velocityInUnits
                * controller.Settings.HomeLastVelocityRatio,
                velocityInUnits,
                velocityInUnits
                * controller.Settings.HomeSecondAccelerationRatio),
            nameof(AjinNative.AxmHomeSetVel));
        AjinController.Check(
            AjinNative.AxmHomeSetStart(axisNumber),
            nameof(AjinNative.AxmHomeSetStart));
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            AjinNative.AxmMoveSStop(axisNumber);
            AjinNative.AxmHomeSetResult(axisNumber, HomeUnknown);
        });
        try
        {
            BeginMotion();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = 0U;
                AjinController.Check(
                    AjinNative.AxmHomeGetResult(axisNumber, ref result),
                    nameof(AjinNative.AxmHomeGetResult));
                PublishPosition();

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
        catch
        {
            AjinNative.AxmMoveSStop(axisNumber);
            AjinNative.AxmHomeSetResult(axisNumber, HomeUnknown);
            throw;
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

        using var homing = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
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
            var result = await Task.WhenAll(
                HomeAxisAsync(MotionAxis.X),
                HomeAxisAsync(MotionAxis.Y));
            return result[0] && result[1];
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    protected override void ResetAlarm()
    {
        foreach (var axis in _axes)
        {
            AjinController.Check(
                AjinNative.AxmSignalServoAlarmReset(axis, 1),
                nameof(AjinNative.AxmSignalServoAlarmReset));
        }

        PublishStateChanged();
    }

    private Task MoveAxisAsync(
        int axis,
        double position,
        double velocity,
        CancellationToken cancellationToken)
    {
        var velocityInUnits = ToUnits(velocity);
        var acceleration = velocityInUnits * controller.Settings.AccelerationMultiplier;

        return RunMoveAsync(
            () => AjinNative.AxmMovePos(
                axis,
                ToUnits(position),
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
        try
        {
            try
            {
                BeginMotion();
                cancellationToken.ThrowIfCancellationRequested();
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
                        inPosition &= Bit(mechanical, InPositionBit);
                        faulted |= Bit(mechanical, AlarmBit)
                                   || Bit(mechanical, EmergencyBit);
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
        try
        {
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
            try
            {
                BeginMotion();
            }
            catch
            {
                StopAxes();
                EndMotion();
                throw;
            }
        }
        catch
        {
            monitor.Dispose();
            throw;
        }
        _ = MonitorJogPositionAsync(axis, monitor);
    }

    private async Task MonitorJogPositionAsync(
        int axis,
        OperationCancellation.Operation monitor)
    {
        using (monitor)
        {
            try
            {
                using var cancellationRegistration =
                    monitor.Token.Register(StopAxes);
                try
                {
                    while (true)
                    {
                        var inMotion = 0U;
                        AjinController.Check(
                            AjinNative.AxmStatusReadInMotion(axis, ref inMotion),
                            nameof(AjinNative.AxmStatusReadInMotion));
                        PublishPosition();
                        if (inMotion == 0)
                        {
                            return;
                        }

                        await Task.Delay(StatusPollInterval, monitor.Token)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    StopAxes();
                    EndMotion();
                    PublishPosition();
                }
            }
            catch (OperationCanceledException) when (monitor.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                PublishFault(exception);
            }
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
