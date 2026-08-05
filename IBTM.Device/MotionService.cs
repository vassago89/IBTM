using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public abstract class MotionService(
    MotionSettings settings,
    bool hasY = true,
    (double Minimum, double Maximum)? xRange = null,
    (double Minimum, double Maximum)? yRange = null,
    (double Minimum, double Maximum)? zRange = null)
{
    private const double SafeZTolerance = 0.05;

    private readonly MotionAxis[] _axes = hasY
        ? [MotionAxis.X, MotionAxis.Y, MotionAxis.Z]
        : [MotionAxis.X, MotionAxis.Z];
    private int _activeMotions;

    public event Action<double, double, double>? PositionChanged;
    public event Action<bool>? MovingChanged;

    public IReadOnlyList<MotionAxis> Axes => _axes;
    public bool HasY => hasY;
    public bool IsMoving => Volatile.Read(ref _activeMotions) > 0;

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
        EnsureHasY();
        ValidateTarget(MotionAxis.X, x);
        ValidateTarget(MotionAxis.Y, y);
        ValidateTarget(MotionAxis.Z, z);
        await MoveToSafeZAsync(cancellationToken);
        await MoveXYCoreAsync(
            x,
            y,
            settings.HorizontalSpeed,
            cancellationToken);
        await MoveZCoreAsync(z, settings.ZSpeed, cancellationToken);
    }

    public async Task MoveToXZAsync(
        double x,
        double z,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(MotionAxis.X, x);
        ValidateTarget(MotionAxis.Z, z);
        await MoveToSafeZAsync(cancellationToken);
        await MoveXCoreAsync(x, settings.HorizontalSpeed, cancellationToken);
        await MoveZCoreAsync(z, settings.ZSpeed, cancellationToken);
    }

    public async Task MoveToXAsync(
        double x,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(MotionAxis.X, x);
        await MoveToSafeZAsync(cancellationToken);
        await MoveXCoreAsync(x, velocity, cancellationToken);
    }

    public async Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureHasY();
        ValidateTarget(MotionAxis.X, x);
        ValidateTarget(MotionAxis.Y, y);
        await MoveToSafeZAsync(cancellationToken);
        await MoveXYCoreAsync(x, y, velocity, cancellationToken);
    }

    public Task MoveToZAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(MotionAxis.Z, z);
        EnsureStopped();
        return MoveZCoreAsync(z, velocity, cancellationToken);
    }

    public Task MoveToSafeZAsync(CancellationToken cancellationToken = default)
    {
        ValidateTarget(MotionAxis.Z, settings.SafeZ);
        EnsureStopped();
        if (!GetAxisState(MotionAxis.Z).Homed)
        {
            throw new InvalidOperationException("Z axis must be homed before moving to Safe Z.");
        }

        return IsAtSafeZ
            ? Task.CompletedTask
            : MoveZCoreAsync(
                settings.SafeZ,
                settings.ZSpeed,
                cancellationToken);
    }

    public Task MoveZToPositiveLimitAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped();
        return MoveZToPositiveLimitCoreAsync(
            Math.Abs(velocity),
            cancellationToken);
    }

    public Task<bool> HomeXFromZPositiveLimitAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        if (IsMoving || !GetAxisState(MotionAxis.X).InPosition)
        {
            throw new InvalidOperationException(
                "A motion command is already running.");
        }

        if (!GetAxisState(MotionAxis.Z).PositiveLimit)
        {
            throw new InvalidOperationException(
                "Z axis must be at its positive limit before homing X.");
        }

        return HomeCoreAsync(
            MotionAxis.X,
            Math.Abs(velocity),
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
        EnsureHasY();
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

    public async Task<bool> HomeAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        if (!_axes.Contains(axis))
        {
            throw new InvalidOperationException(
                $"This motion group has no {axis} axis.");
        }

        EnsureStopped();
        if (axis != MotionAxis.Z)
        {
            if (!GetAxisState(MotionAxis.Z).Homed)
            {
                if (!await HomeCoreAsync(
                    MotionAxis.Z,
                    settings.ZSpeed,
                    cancellationToken))
                {
                    return false;
                }
            }

            await MoveToSafeZAsync(cancellationToken);
        }

        return await HomeCoreAsync(axis, velocity, cancellationToken);
    }

    public Task<bool> HomeHorizontalAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped();
        EnsureSafeZ();
        return HomeHorizontalCoreAsync(velocity, cancellationToken);
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

    protected abstract Task MoveZToPositiveLimitCoreAsync(
        double velocity,
        CancellationToken cancellationToken);

    protected abstract void JogXCore(double velocity);
    protected abstract void JogYCore(double velocity);
    protected abstract void JogZCore(double velocity);

    protected abstract Task<bool> HomeCoreAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task<bool> HomeHorizontalCoreAsync(
        double velocity,
        CancellationToken cancellationToken);

    protected void PublishPositionChanged(double x, double y, double z) =>
        PositionChanged?.Invoke(x, y, z);

    protected void BeginMotion()
    {
        if (Interlocked.Increment(ref _activeMotions) == 1)
        {
            MovingChanged?.Invoke(true);
        }
    }

    protected void EndMotion()
    {
        if (Interlocked.Decrement(ref _activeMotions) == 0)
        {
            MovingChanged?.Invoke(false);
        }
    }

    protected double ClampToRange(MotionAxis axis, double position)
    {
        var range = GetRange(axis);
        return range is null
            ? position
            : Math.Clamp(position, range.Value.Minimum, range.Value.Maximum);
    }

    private void EnsureSafeZ()
    {
        if (!IsAtSafeZ)
        {
            throw new InvalidOperationException(
                $"Horizontal movement requires homed Z at Safe Z ({settings.SafeZ:F3}).");
        }
    }

    private void EnsureHasY()
    {
        if (!hasY)
        {
            throw new InvalidOperationException("This motion group has no Y axis.");
        }
    }

    private void EnsureStopped()
    {
        if (IsMoving || _axes.Any(axis => !GetAxisState(axis).InPosition))
        {
            throw new InvalidOperationException(
                "A motion command is already running.");
        }
    }

    private void ValidateTarget(MotionAxis axis, double position)
    {
        var range = GetRange(axis);
        if (range is not null
            && (position < range.Value.Minimum
                || position > range.Value.Maximum))
        {
            throw new ArgumentOutOfRangeException(
                axis.ToString(),
                position,
                $"{axis} target must be between "
                + $"{range.Value.Minimum:F2} and {range.Value.Maximum:F2} mm.");
        }
    }

    protected (double Minimum, double Maximum)? GetRange(MotionAxis axis) =>
        axis switch
        {
            MotionAxis.X => xRange,
            MotionAxis.Y => yRange,
            MotionAxis.Z => zRange,
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
}
