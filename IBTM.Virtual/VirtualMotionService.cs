using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualMotionService(
    MotionSettings settings,
    bool hasY = true,
    (double Minimum, double Maximum)? xRange = null,
    (double Minimum, double Maximum)? yRange = null,
    (double Minimum, double Maximum)? zRange = null,
    double resolutionMillimeters = 0.01)
    : MotionService(settings, hasY, xRange, yRange, zRange), IDisposable
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(10);

    private readonly bool[] _servoOn = new bool[3];
    private readonly bool[] _homed = new bool[3];
    private CancellationTokenSource? _movement;
    private bool _seekingZPositiveLimit;
    private bool _zPositiveLimit;
    private double _x;
    private double _y;
    private double _z;

    public override void Initialize()
    {
        foreach (var axis in Axes)
        {
            _servoOn[(int)axis] = true;
        }
    }

    protected override Task MoveXYCoreAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default) =>
        SimulateMoveAsync(x, y, _z, velocity, cancellationToken);

    protected override Task MoveXCoreAsync(
        double x,
        double velocity,
        CancellationToken cancellationToken = default) =>
        SimulateMoveAsync(x, _y, _z, velocity, cancellationToken);

    protected override Task MoveZCoreAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken = default) =>
        SimulateMoveAsync(_x, _y, z, velocity, cancellationToken);

    protected override async Task MoveZToPositiveLimitCoreAsync(
        double velocity,
        CancellationToken cancellationToken)
    {
        var maximum = GetRange(MotionAxis.Z)?.Maximum
            ?? throw new InvalidOperationException(
                "Virtual Z maximum is not configured.");
        _seekingZPositiveLimit = true;
        try
        {
            await SimulateMoveAsync(
                _x,
                _y,
                maximum,
                velocity,
                cancellationToken);
        }
        finally
        {
            _seekingZPositiveLimit = false;
        }
    }

    protected override void JogXCore(double velocity) => StartJog(velocity, 0, 0);

    protected override void JogYCore(double velocity) => StartJog(0, velocity, 0);

    protected override void JogZCore(double velocity) => StartJog(0, 0, velocity);

    public override void Stop() => _movement?.Cancel();

    public override void EmergencyStop() => Stop();

    public override void SetServo(MotionAxis axis, bool on) =>
        _servoOn[(int)axis] = on;

    public override (double X, double Y, double Z) GetPosition() => (_x, _y, _z);

    public override AxisState GetAxisState(MotionAxis axis) => new(
        Homed: _homed[(int)axis],
        ServoOn: _servoOn[(int)axis],
        Alarm: false,
        InPosition: !IsMoving,
        Emergency: false,
        HomeSensor: GetCoordinate(axis) == 0,
        PositiveLimit: axis == MotionAxis.Z && _zPositiveLimit,
        NegativeLimit: false);

    protected override async Task<bool> HomeCoreAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _homed[(int)axis] = false;
        switch (axis)
        {
            case MotionAxis.X:
                await SimulateMoveAsync(0, _y, _z, velocity, cancellationToken);
                break;
            case MotionAxis.Y:
                await SimulateMoveAsync(_x, 0, _z, velocity, cancellationToken);
                break;
            case MotionAxis.Z:
                await SimulateMoveAsync(_x, _y, 0, velocity, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(axis));
        }

        _homed[(int)axis] = true;
        return true;
    }

    protected override async Task<bool> HomeHorizontalCoreAsync(
        double velocity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _homed[(int)MotionAxis.X] = false;
        if (HasY)
        {
            _homed[(int)MotionAxis.Y] = false;
        }

        await SimulateMoveAsync(
            0,
            HasY ? 0 : _y,
            _z,
            velocity,
            cancellationToken);
        _homed[(int)MotionAxis.X] = true;
        if (HasY)
        {
            _homed[(int)MotionAxis.Y] = true;
        }

        return true;
    }

    public override void ResetAlarm()
    {
    }

    public void Dispose() => Stop();

    private async Task SimulateMoveAsync(
        double x,
        double y,
        double z,
        double velocity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        x = Quantize(x);
        y = Quantize(y);
        z = Quantize(z);
        var movement = BeginMovement(cancellationToken);
        var startX = _x;
        var startY = _y;
        var startZ = _z;
        var distance = Math.Sqrt(
            Math.Pow(x - startX, 2)
            + Math.Pow(y - startY, 2)
            + Math.Pow(z - startZ, 2));
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
                await Task.Delay(UpdateInterval, movement.Token);
            }

            movement.Token.ThrowIfCancellationRequested();
            SetPosition(x, y, z);
        }
        finally
        {
            EndMovement(movement);
        }
    }

    private void StartJog(double velocityX, double velocityY, double velocityZ)
    {
        var movement = BeginMovement(CancellationToken.None);
        _ = JogAsync(velocityX, velocityY, velocityZ, movement);
    }

    private async Task JogAsync(
        double velocityX,
        double velocityY,
        double velocityZ,
        CancellationTokenSource movement)
    {
        try
        {
            while (true)
            {
                await Task.Delay(UpdateInterval, movement.Token);
                SetPosition(
                    ClampToRange(
                        MotionAxis.X,
                        _x + (velocityX * UpdateInterval.TotalSeconds)),
                    ClampToRange(
                        MotionAxis.Y,
                        _y + (velocityY * UpdateInterval.TotalSeconds)),
                    ClampToRange(
                        MotionAxis.Z,
                        _z + (velocityZ * UpdateInterval.TotalSeconds)));
            }
        }
        catch (OperationCanceledException) when (movement.IsCancellationRequested)
        {
        }
        finally
        {
            EndMovement(movement);
        }
    }

    private CancellationTokenSource BeginMovement(CancellationToken cancellationToken)
    {
        _movement = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        BeginMotion();
        return _movement;
    }

    private void EndMovement(CancellationTokenSource movement)
    {
        _movement = null;
        movement.Dispose();
        EndMotion();
        PublishPositionChanged(_x, _y, _z);
    }

    private void SetPosition(double x, double y, double z)
    {
        _x = Quantize(x);
        _y = Quantize(y);
        _z = Quantize(z);
        var zMaximum = GetRange(MotionAxis.Z)?.Maximum;
        if (_seekingZPositiveLimit
            && zMaximum is not null
            && Math.Abs(_z - zMaximum.Value) <= resolutionMillimeters / 2)
        {
            _zPositiveLimit = true;
        }
        else if (_zPositiveLimit
                 && zMaximum is not null
                 && _z < zMaximum.Value - resolutionMillimeters / 2)
        {
            _zPositiveLimit = false;
        }

        PublishPositionChanged(_x, _y, _z);
    }

    private double Quantize(double position) =>
        Math.Round(position / resolutionMillimeters)
        * resolutionMillimeters;

    private double GetCoordinate(MotionAxis axis) => axis switch
    {
        MotionAxis.X => _x,
        MotionAxis.Y => _y,
        MotionAxis.Z => _z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };
}
