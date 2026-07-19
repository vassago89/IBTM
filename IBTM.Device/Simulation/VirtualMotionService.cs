using System.Diagnostics;

namespace IBTM.Device.Simulation;

/// <summary>Deterministic software motion controller used by simulation mode.</summary>
public sealed class VirtualMotionService : IMotionService, IDisposable
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(10);

    private readonly object _sync = new();
    private CancellationTokenSource? _motionCancellation;

    private int? _axisX;
    private int? _axisY;
    private int? _axisZ;
    private double _x;
    private double _y;
    private double _z;
    private bool _servoOn;
    private bool _disposed;

    public event EventHandler<MotionPositionEventArgs>? PositionChanged;

    public void InitializeAxes(int? axisX, int? axisY, int? axisZ)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _axisX = axisX;
            _axisY = axisY;
            _axisZ = axisZ;
        }
    }

    public void Enable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _servoOn = true;
        }
    }

    public void Disable()
    {
        Stop();
        lock (_sync)
        {
            _servoOn = false;
        }
    }

    public Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveToAsync(x, y, null, velocity, cancellationToken);

    public Task MoveToXAsync(
        double x,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveToAsync(x, null, null, velocity, cancellationToken);

    public Task MoveToYAsync(
        double y,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveToAsync(null, y, null, velocity, cancellationToken);

    public Task MoveToZAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveToAsync(null, null, z, velocity, cancellationToken);

    public void JogX(double velocity) => StartJog(Axis.X, velocity);
    public void JogY(double velocity) => StartJog(Axis.Y, velocity);
    public void JogZ(double velocity) => StartJog(Axis.Z, velocity);

    public void Stop()
    {
        lock (_sync)
        {
            _motionCancellation?.Cancel();
        }
    }

    public void EmergencyStop() => Stop();

    public MotionPosition GetPosition()
    {
        lock (_sync)
        {
            return new MotionPosition(
                _axisX.HasValue ? _x : null,
                _axisY.HasValue ? _y : null,
                _axisZ.HasValue ? _z : null);
        }
    }

    public async Task<bool> HomeXAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        if (_axisX is null)
        {
            return false;
        }

        await MoveToXAsync(0, velocity, cancellationToken);
        return true;
    }

    public async Task<bool> HomeYAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        if (_axisY is null)
        {
            return false;
        }

        await MoveToYAsync(0, velocity, cancellationToken);
        return true;
    }

    public async Task<bool> HomeZAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        if (_axisZ is null)
        {
            return false;
        }

        await MoveToZAsync(0, velocity, cancellationToken);
        return true;
    }

    public void ResetAlarm()
    {
    }

    public MotionStatus? GetXStatus() => GetStatus(_axisX);
    public MotionStatus? GetYStatus() => GetStatus(_axisY);
    public MotionStatus? GetZStatus() => GetStatus(_axisZ);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            _motionCancellation?.Cancel();
            _motionCancellation?.Dispose();
            _motionCancellation = null;
        }
    }

    private async Task MoveToAsync(
        double? targetX,
        double? targetY,
        double? targetZ,
        double velocity,
        CancellationToken cancellationToken)
    {
        ValidateVelocity(velocity);
        ValidateAxes(targetX, targetY, targetZ);

        var motionCancellation = BeginMotion(cancellationToken);
        var token = motionCancellation.Token;
        var start = GetRequiredPosition();
        var end = new Position(
            targetX ?? start.X,
            targetY ?? start.Y,
            targetZ ?? start.Z);

        var distance = Math.Sqrt(
            Math.Pow(end.X - start.X, 2) +
            Math.Pow(end.Y - start.Y, 2) +
            Math.Pow(end.Z - start.Z, 2));

        if (distance == 0)
        {
            CompleteMotion(motionCancellation);
            return;
        }

        var duration = TimeSpan.FromSeconds(distance / velocity);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            while (stopwatch.Elapsed < duration)
            {
                token.ThrowIfCancellationRequested();
                var progress = Math.Clamp(stopwatch.Elapsed.TotalSeconds / duration.TotalSeconds, 0, 1);
                SetPosition(Position.Lerp(start, end, progress));
                await Task.Delay(UpdateInterval, token);
            }

            SetPosition(end);
        }
        finally
        {
            CompleteMotion(motionCancellation);
        }
    }

    private void StartJog(Axis axis, double velocity)
    {
        if (velocity == 0 || !double.IsFinite(velocity))
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), "Jog velocity must be finite and non-zero.");
        }

        ValidateAxis(axis);
        var motionCancellation = BeginMotion(CancellationToken.None);
        _ = JogAsync(axis, velocity, motionCancellation);
    }

    private async Task JogAsync(
        Axis axis,
        double velocity,
        CancellationTokenSource motionCancellation)
    {
        var token = motionCancellation.Token;
        var step = velocity * UpdateInterval.TotalSeconds;

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    switch (axis)
                    {
                        case Axis.X:
                            _x += step;
                            break;
                        case Axis.Y:
                            _y += step;
                            break;
                        case Axis.Z:
                            _z += step;
                            break;
                    }
                }

                RaisePositionChanged();
                await Task.Delay(UpdateInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            CompleteMotion(motionCancellation);
        }
    }

    private CancellationTokenSource BeginMotion(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (!_servoOn)
            {
                throw new InvalidOperationException("The motion group is not enabled.");
            }

            _motionCancellation?.Cancel();
            _motionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return _motionCancellation;
        }
    }

    private void CompleteMotion(CancellationTokenSource motionCancellation)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_motionCancellation, motionCancellation))
            {
                _motionCancellation = null;
            }
        }

        motionCancellation.Dispose();
    }

    private Position GetRequiredPosition()
    {
        lock (_sync)
        {
            return new Position(_x, _y, _z);
        }
    }

    private void SetPosition(Position position)
    {
        lock (_sync)
        {
            _x = position.X;
            _y = position.Y;
            _z = position.Z;
        }

        RaisePositionChanged();
    }

    private void RaisePositionChanged()
    {
        Position position;
        lock (_sync)
        {
            position = new Position(_x, _y, _z);
        }

        PositionChanged?.Invoke(
            this,
            new MotionPositionEventArgs(position.X, position.Y, position.Z));
    }

    private MotionStatus? GetStatus(int? axis) => axis is null
        ? null
        : new MotionStatus(
            IsOriginDone: true,
            IsServoOn: _servoOn,
            IsEmergency: false,
            IsAlarm: false,
            IsInPosition: true,
            IsHome: false,
            IsLimitPositive: false,
            IsLimitNegative: false);

    private void ValidateAxes(double? x, double? y, double? z)
    {
        if (x.HasValue)
        {
            ValidateAxis(Axis.X);
        }

        if (y.HasValue)
        {
            ValidateAxis(Axis.Y);
        }

        if (z.HasValue)
        {
            ValidateAxis(Axis.Z);
        }
    }

    private void ValidateAxis(Axis axis)
    {
        var isConfigured = axis switch
        {
            Axis.X => _axisX.HasValue,
            Axis.Y => _axisY.HasValue,
            Axis.Z => _axisZ.HasValue,
            _ => false,
        };

        if (!isConfigured)
        {
            throw new InvalidOperationException($"Axis {axis} is not configured.");
        }
    }

    private static void ValidateVelocity(double velocity)
    {
        if (!double.IsFinite(velocity) || velocity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), "Velocity must be finite and greater than zero.");
        }
    }

    private enum Axis
    {
        X,
        Y,
        Z,
    }

    private readonly record struct Position(double X, double Y, double Z)
    {
        public static Position Lerp(Position start, Position end, double progress) => new(
            start.X + ((end.X - start.X) * progress),
            start.Y + ((end.Y - start.Y) * progress),
            start.Z + ((end.Z - start.Z) * progress));
    }
}
