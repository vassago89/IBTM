using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualMotionService : MotionService, IDisposable, IMotionDiagnostics
{
    private readonly Func<bool>? _servoPowerOn;
    private static readonly TimeSpan s_updateInterval;
    private static readonly int s_axisCount;
    private readonly (double X, double Y, double Z) _resolution;

    private readonly bool[] _servoOn;
    private readonly bool[] _homed;
    private readonly bool[] _alarm;
    private OperationCancellation.Operation? _movement;
    private double _x;
    private double _y;
    private double _z;

    static VirtualMotionService()
    {
        s_updateInterval = TimeSpan.FromMilliseconds(10);
        s_axisCount = Enum.GetValues<MotionAxis>().Length;
    }

    public VirtualMotionService(
        MotionSettings settings,
        OperationCancellation operationCancellation,
        bool hasY = true,
        bool hasZ = true,
        double resolutionMillimeters = 0.001,
        Func<double>? horizontalZ = null,
        Func<bool>? servoPowerOn = null,
        (double X, double Y, double Z)? axisResolutionMillimeters = null)
        : base(
            settings,
            operationCancellation,
            hasY,
            hasZ,
            horizontalZ)
    {
        _servoPowerOn = servoPowerOn;
        _resolution = axisResolutionMillimeters
            ?? (resolutionMillimeters, resolutionMillimeters, resolutionMillimeters);
        _servoOn = new bool[s_axisCount];
        _homed = new bool[s_axisCount];
        _alarm = new bool[s_axisCount];
    }

    public override bool IsReady => true;

    public override void Initialize()
    {
        foreach (var axis in Axes)
        {
            _servoOn[(int)axis] = _servoPowerOn?.Invoke() ?? true;
        }

        PublishStateChanged();
    }

    protected override Task MoveXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        return SimulateMoveAsync(x, y, _z, velocity, true, cancellationToken);
    }

    protected override Task MoveAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken)
    {
        switch (axis)
        {
            case MotionAxis.X:
                return SimulateMoveAsync(position, _y, _z, velocity, true, cancellationToken);
            case MotionAxis.Y:
                return SimulateMoveAsync(_x, position, _z, velocity, true, cancellationToken);
            case MotionAxis.Z:
                return SimulateMoveAsync(_x, _y, position, velocity, false, cancellationToken);
            default:
                throw new ArgumentOutOfRangeException(nameof(axis));
        }
    }

    public override void SetServo(MotionAxis axis, bool on)
    {
        _servoOn[(int)axis] = on;
        PublishStateChanged();
    }

    public void SetAlarm(MotionAxis axis, bool on)
    {
        _alarm[(int)axis] = on;
        PublishStateChanged();
    }

    public override (double X, double Y, double Z) GetPosition()
    {
        return (_x, _y, _z);
    }

    public (AxisState? State, Exception? Error) ReadDiagnosticState(MotionAxis axis)
    {
        return (GetAxisState(axis), null);
    }

    public (double? Position, Exception? Error) ReadDiagnosticPosition(MotionAxis axis)
    {
        return (GetCoordinate(axis), null);
    }

    public override AxisState GetAxisState(MotionAxis axis)
    {
        return new(
            Homed: _homed[(int)axis],
            ServoOn: _servoOn[(int)axis],
            Alarm: _alarm[(int)axis],
            InPosition: !IsMoving,
            Emergency: false,
            HomeSensor: GetCoordinate(axis) == 0,
            PositiveLimit: false,
            NegativeLimit: false,
            InMotion: IsMoving);
    }

    protected override async Task<bool> HomeAxesAsync(
        MotionAxis[] axes,
        double velocity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var x = _x;
        var y = _y;
        var z = _z;
        foreach (var axis in axes)
        {
            switch (axis)
            {
                case MotionAxis.X:
                    x = 0;
                    break;
                case MotionAxis.Y:
                    y = 0;
                    break;
                case MotionAxis.Z:
                    z = 0;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(axes));
            }
            _homed[(int)axis] = false;
        }

        await SimulateMoveAsync(
            x, y, z, velocity,
            Array.Exists(axes, axis => axis != MotionAxis.Z),
            cancellationToken);
        foreach (var axis in axes)
            _homed[(int)axis] = true;

        PublishStateChanged();
        return true;
    }

    protected override Task ResetAlarmAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Array.Clear(_alarm);
        PublishStateChanged();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _movement?.Cancel();
    }

    public override void Stop()
    {
        _movement?.Cancel();
    }

    private async Task SimulateMoveAsync(
        double x,
        double y,
        double z,
        double velocity,
        bool horizontal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        x = Quantize(x, _resolution.X);
        y = Quantize(y, _resolution.Y);
        z = Quantize(z, _resolution.Z);
        using var movement = BeginMovement(horizontal, cancellationToken);
        var startX = _x;
        var startY = _y;
        var startZ = _z;
        var distance = Math.Sqrt(
            Math.Pow(x - startX, 2) + Math.Pow(y - startY, 2) + Math.Pow(z - startZ, 2));
        var duration = distance / velocity;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            while (stopwatch.Elapsed.TotalSeconds < duration)
            {
                movement.Token.ThrowIfCancellationRequested();
                var progress = stopwatch.Elapsed.TotalSeconds / duration;
                SetPosition(
                    startX + ((x - startX) * progress),
                    startY + ((y - startY) * progress),
                    startZ + ((z - startZ) * progress));
                await Task.Delay(s_updateInterval, movement.Token).ConfigureAwait(false);
            }

            movement.Token.ThrowIfCancellationRequested();
            SetPosition(x, y, z);
        }
        finally
        {
            EndMovement(horizontal);
        }
    }

    public override async Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        ValidateJog(axis, velocity);
        using var movement = BeginMovement(axis != MotionAxis.Z, operation.Token, adjustment: true);
        var startX = _x;
        var startY = _y;
        var startZ = _z;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                await Task.Delay(s_updateInterval, movement.Token).ConfigureAwait(false);
                var distance = velocity * stopwatch.Elapsed.TotalSeconds;
                SetPosition(
                    axis == MotionAxis.X ? startX + distance : startX,
                    axis == MotionAxis.Y ? startY + distance : startY,
                    axis == MotionAxis.Z ? startZ + distance : startZ);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new MotionException("Jog", exception);
        }
        finally
        {
            EndMovement(axis != MotionAxis.Z);
        }
    }

    private OperationCancellation.Operation BeginMovement(
        bool horizontal,
        CancellationToken cancellationToken,
        bool adjustment = false)
    {
        var movement = _movement = Operations.Link(cancellationToken);
        try
        {
            BeginMotion(horizontal, adjustment);
            return movement;
        }
        catch
        {
            using (movement)
            {
                EndMovement(horizontal);
                throw;
            }
        }
    }

    private void EndMovement(bool horizontal)
    {
        _movement = null;
        EndMotion(horizontal);
        PublishPositionChanged(_x, _y, _z);
    }

    private void SetPosition(double x, double y, double z)
    {
        _x = Quantize(x, _resolution.X);
        _y = Quantize(y, _resolution.Y);
        _z = Quantize(z, _resolution.Z);
        PublishPositionChanged(_x, _y, _z);
    }

    private static double Quantize(double position, double resolutionMillimeters)
    {
        return Math.Round(position / resolutionMillimeters) * resolutionMillimeters;
    }

    private double GetCoordinate(MotionAxis axis)
    {
        switch (axis)
        {
            case MotionAxis.X:
                return _x;
            case MotionAxis.Y:
                return _y;
            case MotionAxis.Z:
                return _z;
            default:
                throw new ArgumentOutOfRangeException(nameof(axis));
        }
    }
}
