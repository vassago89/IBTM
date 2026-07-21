using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualMotionService : IMotionService, IDisposable
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(10);

    private CancellationTokenSource? _movement;
    private double _x;
    private double _y;
    private double _z;

    public event Action<double, double, double>? PositionChanged;

    public void Initialize()
    {
    }

    public Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveToAsync(x, y, _z, velocity, cancellationToken);

    public Task MoveToZAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken = default) =>
        MoveToAsync(_x, _y, z, velocity, cancellationToken);

    public void JogX(double velocity) => StartJog(velocity, 0, 0);

    public void JogY(double velocity) => StartJog(0, velocity, 0);

    public void JogZ(double velocity) => StartJog(0, 0, velocity);

    public void Stop() => _movement?.Cancel();

    public void EmergencyStop() => Stop();

    public (double X, double Y, double Z) GetPosition() => (_x, _y, _z);

    public void Dispose() => Stop();

    private async Task MoveToAsync(
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
        Stop();
        _movement = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return _movement;
    }

    private void EndMovement(CancellationTokenSource movement)
    {
        if (ReferenceEquals(_movement, movement))
        {
            _movement = null;
        }

        movement.Dispose();
    }

    private void SetPosition(double x, double y, double z)
    {
        _x = x;
        _y = y;
        _z = z;
        PositionChanged?.Invoke(x, y, z);
    }
}
