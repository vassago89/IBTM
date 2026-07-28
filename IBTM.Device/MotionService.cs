using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public abstract class MotionService(
    StationMotionSettings settings,
    bool hasY = true)
{
    private const double SafeZTolerance = 0.05;
    private readonly MotionAxis[] _axes = hasY
        ? [MotionAxis.X, MotionAxis.Y, MotionAxis.Z]
        : [MotionAxis.X, MotionAxis.Z];

    public event Action<double, double, double>? PositionChanged;

    public IReadOnlyList<MotionAxis> Axes => _axes;
    public bool HasY => hasY;

    public bool IsAtSafeZ =>
        GetAxisState(MotionAxis.Z).Homed
        && Math.Abs(GetPosition().Z - settings.SafeZ) <= SafeZTolerance;

    public abstract void Initialize();

    public async Task MoveToAsync(
        double x,
        double y,
        double z,
        CancellationToken cancellationToken = default)
    {
        await MoveToXYAsync(
            x,
            y,
            settings.HorizontalSpeed,
            cancellationToken);
        await MoveToZAsync(
            z,
            settings.SpeedZ,
            cancellationToken);
    }

    public async Task MoveToXZAsync(
        double x,
        double z,
        CancellationToken cancellationToken = default)
    {
        await MoveToXAsync(
            x,
            settings.HorizontalSpeed,
            cancellationToken);
        await MoveToZAsync(
            z,
            settings.SpeedZ,
            cancellationToken);
    }

    public async Task MoveToXAsync(
        double x,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        await MoveToSafeZAsync(cancellationToken);
        EnsureSafeZ();
        await MoveXCoreAsync(x, velocity, cancellationToken);
    }

    public async Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        if (!hasY)
        {
            throw new InvalidOperationException("This mechanism has no Y axis.");
        }

        await MoveToSafeZAsync(cancellationToken);
        EnsureSafeZ();
        await MoveXYCoreAsync(x, y, velocity, cancellationToken);
    }

    public Task MoveToZAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped();
        return MoveZCoreAsync(z, velocity, cancellationToken);
    }

    public Task MoveToSafeZAsync(CancellationToken cancellationToken = default)
    {
        EnsureStopped();
        if (!GetAxisState(MotionAxis.Z).Homed)
        {
            throw new InvalidOperationException("Z axis must be homed before moving to Safe Z.");
        }

        return IsAtSafeZ
            ? Task.CompletedTask
            : MoveZCoreAsync(
                settings.SafeZ,
                settings.SpeedZ,
                cancellationToken);
    }

    public void JogX(double velocity)
    {
        EnsureStopped();
        EnsureSafeZ();
        JogXCore(velocity);
    }

    public void JogY(double velocity)
    {
        if (!hasY)
        {
            throw new InvalidOperationException("This mechanism has no Y axis.");
        }

        EnsureStopped();
        EnsureSafeZ();
        JogYCore(velocity);
    }

    public void JogZ(double velocity)
    {
        EnsureStopped();
        JogZCore(velocity);
    }

    public abstract void Stop();
    public abstract void EmergencyStop();
    public abstract void SetServo(MotionAxis axis, bool on);
    public abstract (double X, double Y, double Z) GetPosition();
    public abstract AxisState GetAxisState(MotionAxis axis);

    public async Task HomeAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        if (!_axes.Contains(axis))
        {
            throw new InvalidOperationException(
                $"This mechanism has no {axis} axis.");
        }

        EnsureStopped();
        if (axis != MotionAxis.Z)
        {
            if (!GetAxisState(MotionAxis.Z).Homed)
            {
                await HomeCoreAsync(
                    MotionAxis.Z,
                    settings.SpeedZ,
                    cancellationToken);
            }

            await MoveToSafeZAsync(cancellationToken);
            EnsureSafeZ();
        }

        await HomeCoreAsync(axis, velocity, cancellationToken);
    }

    public abstract void ResetAlarm();

    protected abstract Task MoveXYCoreAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task MoveXCoreAsync(
        double x,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task MoveZCoreAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract void JogXCore(double velocity);
    protected abstract void JogYCore(double velocity);
    protected abstract void JogZCore(double velocity);

    protected abstract Task HomeCoreAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken);

    protected void PublishPositionChanged(double x, double y, double z) =>
        PositionChanged?.Invoke(x, y, z);

    private void EnsureSafeZ()
    {
        if (!IsAtSafeZ)
        {
            throw new InvalidOperationException(
                $"Horizontal movement requires homed Z at Safe Z ({settings.SafeZ:F3}).");
        }
    }

    private void EnsureStopped()
    {
        if (_axes.Any(axis => !GetAxisState(axis).InPosition))
        {
            throw new InvalidOperationException(
                "A motion command is already running.");
        }
    }
}
