using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualMotionService(
    StationMotionSettings settings,
    bool hasY = true)
    : MotionService(settings, hasY), IDisposable
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(10);

    private readonly bool[] _servoOn = new bool[3];
    private readonly bool[] _homed = new bool[3];
    private CancellationTokenSource? _movement;
    private double _x;
    private double _y;
    private double _z;

    public override void Initialize()
    {
        foreach (var axis in Axes)
        {
            _servoOn[(int)axis] = true;
            _homed[(int)axis] = true;
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
        InPosition: _movement is null,
        Emergency: false,
        HomeSensor: GetCoordinate(axis) == 0,
        PositiveLimit: false,
        NegativeLimit: false);

    protected override async Task HomeCoreAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
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
                    _x + (velocityX * UpdateInterval.TotalSeconds),
                    _y + (velocityY * UpdateInterval.TotalSeconds),
                    _z + (velocityZ * UpdateInterval.TotalSeconds));
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
        return _movement;
    }

    private void EndMovement(CancellationTokenSource movement)
    {
        _movement = null;
        movement.Dispose();
        PublishPositionChanged(_x, _y, _z);
    }

    private void SetPosition(double x, double y, double z)
    {
        _x = x;
        _y = y;
        _z = z;
        PublishPositionChanged(x, y, z);
    }

    private double GetCoordinate(MotionAxis axis) => axis switch
    {
        MotionAxis.X => _x,
        MotionAxis.Y => _y,
        MotionAxis.Z => _z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };
}
