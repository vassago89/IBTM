using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public enum MotionCommand
{
    [Description("Idle")]
    None,
    [Description("Positioning")]
    Positioning,
    [Description("Manual Adjustment")]
    Adjustment,
}

public interface IMotionFeedback
{
    event Action<double, double, double>? PositionChanged;
    event Action<bool>? MovingChanged;
    event Action? StateChanged;

    IReadOnlyList<MotionAxis> Axes { get; }

    bool IsReady { get; }

    bool HasY { get; }

    bool HasZ { get; }

    bool IsMoving { get; }

    bool IsMovingHorizontal { get; }

    MotionCommand Command { get; }

    bool IsAtHorizontalZ { get; }

    (double X, double Y, double Z) GetPosition();
    AxisState GetAxisState(MotionAxis axis);
}

public interface IAxisMotion : IMotionFeedback
{
    void Initialize();
    Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default);
    Task MoveXAtClearZAsync(
        double x,
        double clearZ,
        double velocity,
        CancellationToken cancellationToken = default);
    Task MoveToHorizontalZAsync(CancellationToken cancellationToken = default);
    Task MoveZToPositiveLimitAsync(double velocity, CancellationToken cancellationToken = default);
    Task<bool> HomeFromZPositiveLimitAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default);
    Task<bool> HomeAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default);
    Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default,
        bool atCurrentHeight = false);
    void Reset();
    void SetServo(MotionAxis axis, bool on);
}

public interface IXyMotion : IAxisMotion
{
    Task AdjustAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default);
    Task MoveToAsync(double x, double y, double z, CancellationToken cancellationToken = default);
    Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default);
    Task<bool> HomeHorizontalAsync(double velocity, CancellationToken cancellationToken = default);
}

public abstract class MotionService(
    MotionSettings settings,
    OperationCancellation operationCancellation,
    bool hasY = true,
    bool hasZ = true,
    Func<double>? horizontalZ = null) : IXyMotion
{
    public const double PositionToleranceMillimeters = 0.05;
    protected MotionSettings Settings { get; } = settings;

    private readonly MotionAxis[] _axes = (hasY, hasZ) switch
    {
        (true, true) => [MotionAxis.X, MotionAxis.Y, MotionAxis.Z],
        (true, false) => [MotionAxis.X, MotionAxis.Y],
        (false, true) => [MotionAxis.X, MotionAxis.Z],
        _ => [MotionAxis.X],
    };
    protected OperationCancellation Operations { get; } = operationCancellation;

    private int _activeMotions;
    private int _activeHorizontalMotions;
    private MotionCommand _command = MotionCommand.Positioning;

    public event Action<double, double, double>? PositionChanged;
    public event Action<bool>? MovingChanged;
    public event Action? StateChanged;

    public IReadOnlyList<MotionAxis> Axes
    {
        get
        {
            return _axes;
        }
    }

    public abstract bool IsReady { get; }

    public bool HasY
    {
        get
        {
            return hasY;
        }
    }

    public bool HasZ
    {
        get
        {
            return hasZ;
        }
    }

    public virtual bool IsMoving
    {
        get
        {
            return Volatile.Read(ref _activeMotions) > 0;
        }
    }

    public virtual bool IsMovingHorizontal
    {
        get
        {
            return Volatile.Read(ref _activeHorizontalMotions) > 0;
        }
    }

    public MotionCommand Command
    {
        get
        {
            // Command ownership is not hardware movement: external moves have no local command.
            return Volatile.Read(ref _activeMotions) > 0 ? _command : MotionCommand.None;
        }
    }

    private double HorizontalZ
    {
        get
        {
            return horizontalZ!();
        }
    }

    public bool IsAtHorizontalZ
    {
        get
        {
            return !hasZ
                || GetAxisState(MotionAxis.Z).Homed
                && Math.Abs(GetPosition().Z - HorizontalZ) <= PositionToleranceMillimeters;
        }
    }

    public abstract void Initialize();

    public async Task AdjustAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        EnsureStopped();
        if (axis == MotionAxis.Y)
            EnsureHasY();
        if (axis == MotionAxis.Z)
            EnsureHasZ();
        ValidateTarget(axis, position);
        _command = MotionCommand.Adjustment;
        try
        {
            await MoveAxisCoreAsync(axis, position, velocity, operation.Token);
        }
        finally
        {
            if (Volatile.Read(ref _activeMotions) == 0)
                _command = MotionCommand.Positioning;
        }
    }

    public async Task MoveToAsync(
        double x,
        double y,
        double z,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasY();
        EnsureHasZ();
        ValidateTarget(MotionAxis.X, x);
        ValidateTarget(MotionAxis.Y, y);
        ValidateTarget(MotionAxis.Z, z);
        await MoveToHorizontalZAsync(cancellationToken);
        await MoveXYCoreAsync(x, y, Settings.HorizontalSpeed, cancellationToken);
        await MoveAxisCoreAsync(MotionAxis.Z, z, Settings.ZSpeed, cancellationToken);
    }

    public async Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        if (axis == MotionAxis.Y)
            EnsureHasY();
        else if (axis == MotionAxis.Z)
            EnsureHasZ();
        else if (axis != MotionAxis.X)
            throw new ArgumentOutOfRangeException(nameof(axis));

        ValidateTarget(axis, position);
        if (axis != MotionAxis.Z && hasZ)
        {
            await MoveToHorizontalZAsync(cancellationToken);
        }
        else
        {
            EnsureStopped();
        }

        await MoveAxisCoreAsync(axis, position, velocity, cancellationToken);
    }

    public async Task MoveXAtClearZAsync(
        double x,
        double clearZ,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasZ();
        ValidateTarget(MotionAxis.X, x);
        ValidateTarget(MotionAxis.Z, clearZ);
        EnsureStopped();
        if (!GetAxisState(MotionAxis.Z).Homed
            || Math.Abs(GetPosition().Z - clearZ) > PositionToleranceMillimeters)
        {
            throw new InvalidOperationException($"X movement requires Z at Clear Z ({clearZ:F3}).");
        }

        await MoveAxisCoreAsync(MotionAxis.X, x, velocity, cancellationToken);
    }

    public async Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasY();
        ValidateTarget(MotionAxis.X, x);
        ValidateTarget(MotionAxis.Y, y);
        if (hasZ)
        {
            await MoveToHorizontalZAsync(cancellationToken);
        }
        else
        {
            EnsureStopped();
        }

        await MoveXYCoreAsync(x, y, velocity, cancellationToken);
    }

    public async Task MoveToHorizontalZAsync(CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasZ();
        ValidateTarget(MotionAxis.Z, HorizontalZ);
        EnsureStopped();
        if (!GetAxisState(MotionAxis.Z).Homed)
        {
            throw new InvalidOperationException("Z axis must be homed before moving to its reference.");
        }

        if (!IsAtHorizontalZ)
        {
            await MoveAxisCoreAsync(MotionAxis.Z, HorizontalZ, Settings.ZSpeed, cancellationToken);
        }
    }

    public async Task MoveZToPositiveLimitAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasZ();
        EnsureStopped();
        await MoveZToPositiveLimitCoreAsync(Math.Abs(velocity), cancellationToken);
    }

    public async Task<bool> HomeFromZPositiveLimitAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        EnsureHasZ();
        EnsureStopped();

        if (axis is not MotionAxis.X and not MotionAxis.Y || axis == MotionAxis.Y && !hasY)
        {
            throw new InvalidOperationException($"This motion group has no horizontal {axis} axis.");
        }

        if (!GetAxisState(MotionAxis.Z).PositiveLimit)
        {
            throw new InvalidOperationException("Z axis must be at its positive limit before horizontal homing.");
        }

        return await HomeCoreAsync(axis, Math.Abs(velocity), cancellationToken);
    }

    public async Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default,
        bool atCurrentHeight = false)
    {
        if (axis is not (MotionAxis.X or MotionAxis.Y or MotionAxis.Z))
            throw new ArgumentOutOfRangeException(nameof(axis));
        using var operation = Operations.Link(cancellationToken);
        if (axis == MotionAxis.Y)
            EnsureHasY();
        if (axis == MotionAxis.Z)
            EnsureHasZ();
        EnsureStopped();
        if (axis != MotionAxis.Z && !atCurrentHeight)
            EnsureHorizontalZ();
        if (atCurrentHeight)
            _command = MotionCommand.Adjustment;
        try
        {
            await JogCoreAsync(axis, velocity, operation.Token);
        }
        finally
        {
            if (Volatile.Read(ref _activeMotions) == 0)
                _command = MotionCommand.Positioning;
        }
    }

    public abstract void SetServo(MotionAxis axis, bool on);
    public abstract (double X, double Y, double Z) GetPosition();
    public abstract AxisState GetAxisState(MotionAxis axis);

    public async Task<bool> HomeAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        if (!_axes.Contains(axis))
        {
            throw new InvalidOperationException($"This motion group has no {axis} axis.");
        }

        EnsureStopped();
        if (axis != MotionAxis.Z && hasZ)
        {
            if (!GetAxisState(MotionAxis.Z).Homed)
            {
                if (!await HomeCoreAsync(MotionAxis.Z, Settings.ZSpeed, cancellationToken))
                {
                    return false;
                }
            }

            await MoveToHorizontalZAsync(cancellationToken);
        }

        return await HomeCoreAsync(axis, velocity, cancellationToken);
    }

    public async Task<bool> HomeHorizontalAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        EnsureStopped();
        EnsureHorizontalZ();
        return await HomeHorizontalCoreAsync(velocity, cancellationToken);
    }

    protected abstract void ResetAlarm();

    public void Reset()
    {
        ResetAlarm();
        foreach (var axis in _axes)
        {
            SetServo(axis, true);
        }
    }

    protected abstract Task MoveXYCoreAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task MoveAxisCoreAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task MoveZToPositiveLimitCoreAsync(
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task JogCoreAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task<bool> HomeCoreAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task<bool> HomeHorizontalCoreAsync(
        double velocity,
        CancellationToken cancellationToken);

    protected void PublishPositionChanged(double x, double y, double z)
    {
        PositionChanged?.Invoke(x, y, z);
    }

    protected void PublishStateChanged()
    {
        StateChanged?.Invoke();
    }

    protected void BeginMotion(bool horizontal)
    {
        if (horizontal)
            Interlocked.Increment(ref _activeHorizontalMotions);
        if (Interlocked.Increment(ref _activeMotions) == 1)
        {
            MovingChanged?.Invoke(true);
            PublishStateChanged();
        }
    }

    protected void EndMotion(bool horizontal)
    {
        if (horizontal)
            Interlocked.Decrement(ref _activeHorizontalMotions);
        if (Interlocked.Decrement(ref _activeMotions) == 0)
        {
            _command = MotionCommand.Positioning;
            MovingChanged?.Invoke(false);
            PublishStateChanged();
        }
    }

    private void EnsureHorizontalZ()
    {
        if (!IsAtHorizontalZ)
        {
            throw new InvalidOperationException(
                $"Horizontal movement requires homed Z at {HorizontalZ:F3}.");
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
        if (Volatile.Read(ref _activeMotions) > 0
            || IsMoving
            || _axes.Any(axis => !GetAxisState(axis).InPosition))
        {
            throw new InvalidOperationException("A motion command is already running.");
        }
    }

    private void ValidateTarget(MotionAxis axis, double position)
    {
        if (!double.IsFinite(position))
        {
            throw new ArgumentOutOfRangeException(
                axis.ToString(),
                position,
                $"{axis} target must be a finite coordinate.");
        }
    }
}
