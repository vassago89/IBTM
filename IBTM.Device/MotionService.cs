using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public interface IAxisMotion
{
    event Action<double, double, double>? PositionChanged;
    event Action<bool>? MovingChanged;

    IReadOnlyList<MotionAxis> Axes { get; }
    bool HasY { get; }
    bool HasZ { get; }
    bool IsMoving { get; }
    bool IsAtSafeZ { get; }

    void Initialize();
    Task MoveXAsync(
        double x,
        double velocity,
        CancellationToken cancellationToken = default);
    Task MoveYAsync(
        double y,
        double velocity,
        CancellationToken cancellationToken = default);
    Task MoveZAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken = default);
    Task MoveXAtClearZAsync(
        double x,
        double clearZ,
        double velocity,
        CancellationToken cancellationToken = default);
    Task MoveToSafeZAsync(CancellationToken cancellationToken = default);
    Task MoveZToPositiveLimitAsync(
        double velocity,
        CancellationToken cancellationToken = default);
    Task<bool> HomeFromZPositiveLimitAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default);
    Task<bool> HomeAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default);
    void JogX(double velocity, CancellationToken cancellationToken = default);
    void JogY(double velocity, CancellationToken cancellationToken = default);
    void JogZ(double velocity, CancellationToken cancellationToken = default);
    void SetServo(MotionAxis axis, bool on);
    (double X, double Y, double Z) GetPosition();
    AxisState GetAxisState(MotionAxis axis);
    void ResetAlarm();
}

public interface IXyMotion : IAxisMotion
{
    Task MoveToAsync(
        double x,
        double y,
        double z,
        CancellationToken cancellationToken = default);
    Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default);
    Task<bool> HomeHorizontalAsync(
        double velocity,
        CancellationToken cancellationToken = default);
}

public abstract class MotionService(
    MotionSettings settings,
    OperationCancellation operationCancellation,
    bool hasY = true,
    bool hasZ = true,
    (double Minimum, double Maximum)? xRange = null,
    (double Minimum, double Maximum)? yRange = null,
    (double Minimum, double Maximum)? zRange = null) : IXyMotion
{
    private const double PositionTolerance = 0.05;

    private readonly MotionAxis[] _axes = (hasY, hasZ) switch
    {
        (true, true) => [MotionAxis.X, MotionAxis.Y, MotionAxis.Z],
        (true, false) => [MotionAxis.X, MotionAxis.Y],
        (false, true) => [MotionAxis.X, MotionAxis.Z],
        _ => [MotionAxis.X],
    };
    private readonly OperationCancellation _operationCancellation =
        operationCancellation;
    private int _activeMotions;

    public event Action<double, double, double>? PositionChanged;
    public event Action<bool>? MovingChanged;

    public IReadOnlyList<MotionAxis> Axes => _axes;
    public bool HasY => hasY;
    public bool HasZ => hasZ;
    public bool IsMoving => Volatile.Read(ref _activeMotions) > 0;

    public bool IsAtSafeZ =>
        !hasZ
        || GetAxisState(MotionAxis.Z).Homed
        && Math.Abs(GetPosition().Z - settings.SafeZ) <= PositionTolerance;

    public abstract void Initialize();

    public async Task MoveToAsync(
        double x,
        double y,
        double z,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasY();
        EnsureHasZ();
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

    public async Task MoveXAsync(
        double x,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        cancellationToken = operation.Token;
        ValidateTarget(MotionAxis.X, x);
        if (hasZ)
        {
            await MoveToSafeZAsync(cancellationToken);
        }
        else
        {
            EnsureStopped();
        }

        await MoveXCoreAsync(x, velocity, cancellationToken);
    }

    public async Task MoveYAsync(
        double y,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasY();
        ValidateTarget(MotionAxis.Y, y);
        if (hasZ)
        {
            await MoveToSafeZAsync(cancellationToken);
        }
        else
        {
            EnsureStopped();
        }

        await MoveYCoreAsync(y, velocity, cancellationToken);
    }

    public async Task MoveXAtClearZAsync(
        double x,
        double clearZ,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasZ();
        ValidateTarget(MotionAxis.X, x);
        ValidateTarget(MotionAxis.Z, clearZ);
        EnsureStopped();
        if (!GetAxisState(MotionAxis.Z).Homed
            || Math.Abs(GetPosition().Z - clearZ) > PositionTolerance)
        {
            throw new InvalidOperationException(
                $"X movement requires Z at Clear Z ({clearZ:F3}).");
        }

        await MoveXCoreAsync(x, velocity, cancellationToken);
    }

    public async Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasY();
        ValidateTarget(MotionAxis.X, x);
        ValidateTarget(MotionAxis.Y, y);
        if (hasZ)
        {
            await MoveToSafeZAsync(cancellationToken);
        }
        else
        {
            EnsureStopped();
        }
        await MoveXYCoreAsync(x, y, velocity, cancellationToken);
    }

    public async Task MoveZAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasZ();
        ValidateTarget(MotionAxis.Z, z);
        EnsureStopped();
        await MoveZCoreAsync(z, velocity, cancellationToken);
    }

    public async Task MoveToSafeZAsync(
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasZ();
        ValidateTarget(MotionAxis.Z, settings.SafeZ);
        EnsureStopped();
        if (!GetAxisState(MotionAxis.Z).Homed)
        {
            throw new InvalidOperationException("Z axis must be homed before moving to Safe Z.");
        }

        if (!IsAtSafeZ)
        {
            await MoveZCoreAsync(
                settings.SafeZ,
                settings.ZSpeed,
                cancellationToken);
        }
    }

    public async Task MoveZToPositiveLimitAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasZ();
        EnsureStopped();
        await MoveZToPositiveLimitCoreAsync(
            Math.Abs(velocity),
            cancellationToken);
    }

    public async Task<bool> HomeFromZPositiveLimitAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasZ();
        EnsureStopped();

        if (axis is not MotionAxis.X and not MotionAxis.Y
            || axis == MotionAxis.Y && !hasY)
        {
            throw new InvalidOperationException(
                $"This motion group has no horizontal {axis} axis.");
        }

        if (!GetAxisState(MotionAxis.Z).PositiveLimit)
        {
            throw new InvalidOperationException(
                "Z axis must be at its positive limit before horizontal homing.");
        }

        return await HomeCoreAsync(
            axis,
            Math.Abs(velocity),
            cancellationToken);
    }

    public void JogX(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureStopped();
        EnsureSafeZ();
        JogXCore(velocity, cancellationToken);
    }

    public void JogY(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureHasY();
        EnsureStopped();
        EnsureSafeZ();
        JogYCore(velocity, cancellationToken);
    }

    public void JogZ(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureHasZ();
        EnsureStopped();
        JogZCore(velocity, cancellationToken);
    }

    public abstract void SetServo(MotionAxis axis, bool on);
    public abstract (double X, double Y, double Z) GetPosition();
    public abstract AxisState GetAxisState(MotionAxis axis);

    public async Task<bool> HomeAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        cancellationToken = operation.Token;
        if (!_axes.Contains(axis))
        {
            throw new InvalidOperationException(
                $"This motion group has no {axis} axis.");
        }

        EnsureStopped();
        if (axis != MotionAxis.Z && hasZ)
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

    public async Task<bool> HomeHorizontalAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkOperation(cancellationToken);
        cancellationToken = operation.Token;
        EnsureStopped();
        EnsureSafeZ();
        return await HomeHorizontalCoreAsync(velocity, cancellationToken);
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

    protected abstract Task MoveYCoreAsync(
        double y,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task MoveZCoreAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task MoveZToPositiveLimitCoreAsync(
        double velocity,
        CancellationToken cancellationToken);

    protected abstract void JogXCore(
        double velocity,
        CancellationToken cancellationToken);
    protected abstract void JogYCore(
        double velocity,
        CancellationToken cancellationToken);
    protected abstract void JogZCore(
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task<bool> HomeCoreAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task<bool> HomeHorizontalCoreAsync(
        double velocity,
        CancellationToken cancellationToken);

    protected void PublishPositionChanged(double x, double y, double z) =>
        PositionChanged?.Invoke(x, y, z);

    protected CancellationTokenSource LinkOperation(
        CancellationToken cancellationToken = default) =>
        _operationCancellation.Link(cancellationToken);

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

    private void EnsureHasZ()
    {
        if (!hasZ)
        {
            throw new InvalidOperationException("This motion group has no Z axis.");
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
