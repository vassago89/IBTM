using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualMotionService(
    MotionSettings settings,
    OperationCancellation operationCancellation,
    bool hasY = true,
    bool hasZ = true,
    (double Minimum, double Maximum)? xRange = null,
    (double Minimum, double Maximum)? yRange = null,
    (double Minimum, double Maximum)? zRange = null,
    double resolutionMillimeters =
        MotionHardwareSettings.DefaultMillimetersPerPulse,
    Func<double>? horizontalZ = null,
    Func<bool>? servoPowerOn = null)
    : MotionService(
        settings,
        operationCancellation,
        hasY,
        hasZ,
        horizontalZ,
        xRange,
        yRange,
        zRange), IDisposable, IMotionDiagnostics
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(10);
    private static readonly int AxisCount = Enum.GetValues<MotionAxis>().Length;

    private readonly bool[] _servoOn = new bool[AxisCount];
    private readonly bool[] _homed = new bool[AxisCount];
    private readonly bool[] _alarm = new bool[AxisCount];
    private OperationCancellation.Operation? _movement;
    private bool _seekingZPositiveLimit;
    private bool _zPositiveLimit;
    private double _x;
    private double _y;
    private double _z;

    public override bool IsReady => true;

    public override void Initialize()
    {
        foreach (var axis in Axes)
        {
            _servoOn[(int)axis] = servoPowerOn?.Invoke() ?? true;
        }
        PublishStateChanged();
    }

    protected override Task MoveXYCoreAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default) =>
        SimulateMoveAsync(x, y, _z, velocity, true, cancellationToken);

    protected override Task MoveAxisCoreAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken) => axis switch
        {
            MotionAxis.X => SimulateMoveAsync(position, _y, _z, velocity, true, cancellationToken),
            MotionAxis.Y => SimulateMoveAsync(_x, position, _z, velocity, true, cancellationToken),
            MotionAxis.Z => SimulateMoveAsync(_x, _y, position, velocity, false, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };

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
                false,
                cancellationToken);
        }
        finally
        {
            _seekingZPositiveLimit = false;
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

    public override (double X, double Y, double Z) GetPosition() => (_x, _y, _z);

    public AxisState ReadDiagnosticState(MotionAxis axis) => GetAxisState(axis);
    public double ReadDiagnosticPosition(MotionAxis axis) => GetCoordinate(axis);

    public override AxisState GetAxisState(MotionAxis axis) => new(
        Homed: _homed[(int)axis],
        ServoOn: _servoOn[(int)axis],
        Alarm: _alarm[(int)axis],
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
                await SimulateMoveAsync(0, _y, _z, velocity, true, cancellationToken);
                break;
            case MotionAxis.Y:
                await SimulateMoveAsync(_x, 0, _z, velocity, true, cancellationToken);
                break;
            case MotionAxis.Z:
                await SimulateMoveAsync(_x, _y, 0, velocity, false, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(axis));
        }

        _homed[(int)axis] = true;
        PublishStateChanged();
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
            true,
            cancellationToken);
        _homed[(int)MotionAxis.X] = true;
        if (HasY)
        {
            _homed[(int)MotionAxis.Y] = true;
        }

        PublishStateChanged();
        return true;
    }

    protected override void ResetAlarm()
    {
        Array.Clear(_alarm);
        PublishStateChanged();
    }

    public void Dispose() => _movement?.Cancel();

    private async Task SimulateMoveAsync(
        double x,
        double y,
        double z,
        double velocity,
        bool horizontal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        x = Quantize(x);
        y = Quantize(y);
        z = Quantize(z);
        using var movement = BeginMovement(horizontal, cancellationToken);
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
                await Task.Delay(UpdateInterval, movement.Token)
                    .ConfigureAwait(false);
            }

            movement.Token.ThrowIfCancellationRequested();
            SetPosition(x, y, z);
        }
        finally
        {
            EndMovement(horizontal);
        }
    }

    protected override async Task JogCoreAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken)
    {
        using var movement = BeginMovement(axis != MotionAxis.Z, cancellationToken);
        var startX = _x;
        var startY = _y;
        var startZ = _z;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                await Task.Delay(UpdateInterval, movement.Token).ConfigureAwait(false);
                var distance = velocity * stopwatch.Elapsed.TotalSeconds;
                SetPosition(
                    axis == MotionAxis.X ? ClampToRange(axis, startX + distance) : startX,
                    axis == MotionAxis.Y ? ClampToRange(axis, startY + distance) : startY,
                    axis == MotionAxis.Z ? ClampToRange(axis, startZ + distance) : startZ);
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
        CancellationToken cancellationToken)
    {
        var movement = _movement = Operations.Link(cancellationToken);
        try
        {
            BeginMotion(horizontal);
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
