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
    void Stop();
    Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default);
    Task MoveToHorizontalZAsync(CancellationToken cancellationToken = default, double? travelZ = null);
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
        CancellationToken cancellationToken = default,
        double? travelZ = null);
    Task<bool> HomeHorizontalAsync(double velocity, CancellationToken cancellationToken = default);
}

public abstract class MotionService : IXyMotion
{
    public const double PositionToleranceMillimeters = 0.05;

    private readonly Func<double>? _horizontalZ;
    private int _activeMotions;
    private int _activeHorizontalMotions;
    private MotionCommand _command = MotionCommand.Positioning;

    protected MotionService(
        MotionSettings settings,
        OperationCancellation operationCancellation,
        bool hasY = true,
        bool hasZ = true,
        Func<double>? horizontalZ = null)
    {
        Settings = settings;
        Operations = operationCancellation;
        HasY = hasY;
        HasZ = hasZ;
        _horizontalZ = horizontalZ;
        Axes = (hasY, hasZ) switch
        {
            (true, true) => new[] { MotionAxis.X, MotionAxis.Y, MotionAxis.Z },
            (true, false) => new[] { MotionAxis.X, MotionAxis.Y },
            (false, true) => new[] { MotionAxis.X, MotionAxis.Z },
            _ => new[] { MotionAxis.X },
        };
    }

    public event Action<double, double, double>? PositionChanged;
    public event Action<bool>? MovingChanged;
    public event Action? StateChanged;

    public IReadOnlyList<MotionAxis> Axes { get; }

    public abstract bool IsReady { get; }

    public bool HasY { get; }
    public bool HasZ { get; }
    protected MotionSettings Settings { get; }
    protected OperationCancellation Operations { get; }

    public virtual bool IsMoving => Volatile.Read(ref _activeMotions) > 0;

    public virtual bool IsMovingHorizontal => Volatile.Read(ref _activeHorizontalMotions) > 0;

    // Command ownership is not hardware movement: external moves have no local command.
    public MotionCommand Command => Volatile.Read(ref _activeMotions) > 0 ? _command : MotionCommand.None;

    private double HorizontalZ => _horizontalZ!();

    public bool IsAtHorizontalZ
    {
        get
        {
            return !HasZ
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
        ValidatePositive(velocity, nameof(velocity));
        EnsureStopped();
        if (axis == MotionAxis.Y)
            EnsureHasY();
        if (axis == MotionAxis.Z)
            EnsureHasZ();
        ValidateTarget(axis, position);
        _command = MotionCommand.Adjustment;
        try
        {
            await MoveAsync(axis, position, velocity, operation.Token);
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
        ValidatePositive(Settings.HorizontalSpeed, nameof(Settings.HorizontalSpeed));
        EnsureHasY();
        ValidateTarget(MotionAxis.X, x);
        ValidateTarget(MotionAxis.Y, y);
        ValidateTarget(MotionAxis.Z, z);
        await MoveToHorizontalZAsync(cancellationToken);
        await MoveXYAsync(x, y, Settings.HorizontalSpeed, cancellationToken);
        await MoveAsync(MotionAxis.Z, z, Settings.ZSpeed, cancellationToken);
    }

    public async Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        ValidatePositive(velocity, nameof(velocity));
        if (axis == MotionAxis.Y)
            EnsureHasY();
        else if (axis == MotionAxis.Z)
            EnsureHasZ();
        else if (axis != MotionAxis.X)
            throw new ArgumentOutOfRangeException(nameof(axis));

        ValidateTarget(axis, position);
        if (axis != MotionAxis.Z && HasZ)
        {
            await MoveToHorizontalZAsync(cancellationToken);
        }
        else
        {
            EnsureStopped();
        }

        await MoveAsync(axis, position, velocity, cancellationToken);
    }

    public async Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default,
        double? travelZ = null)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        ValidatePositive(velocity, nameof(velocity));
        EnsureHasY();
        ValidateTarget(MotionAxis.X, x);
        ValidateTarget(MotionAxis.Y, y);
        if (HasZ)
        {
            await MoveToHorizontalZAsync(cancellationToken, travelZ);
        }
        else
        {
            EnsureStopped();
        }

        await MoveXYAsync(x, y, velocity, cancellationToken);
    }

    public async Task MoveToHorizontalZAsync(CancellationToken cancellationToken = default, double? travelZ = null)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        ValidatePositive(Settings.ZSpeed, nameof(Settings.ZSpeed));
        EnsureHasZ();
        var targetZ = travelZ ?? HorizontalZ;
        ValidateTarget(MotionAxis.Z, targetZ);
        EnsureStopped();
        if (!GetAxisState(MotionAxis.Z).Homed)
        {
            throw new MotionInterlockException("Z axis must be homed before moving to its reference.");
        }

        if (Math.Abs(GetPosition().Z - targetZ) > PositionToleranceMillimeters)
        {
            await MoveAsync(MotionAxis.Z, targetZ, Settings.ZSpeed, cancellationToken);
        }
    }

    public abstract Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default,
        bool atCurrentHeight = false);

    protected void ValidateJog(MotionAxis axis, double velocity, bool atCurrentHeight)
    {
        if (axis is not (MotionAxis.X or MotionAxis.Y or MotionAxis.Z))
            throw new ArgumentOutOfRangeException(nameof(axis));
        ValidatePositive(Math.Abs(velocity), nameof(velocity));
        if (axis == MotionAxis.Y)
            EnsureHasY();
        if (axis == MotionAxis.Z)
            EnsureHasZ();
        EnsureStopped();
        if (axis != MotionAxis.Z && !atCurrentHeight)
            EnsureHorizontalZ();
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
        if (!Axes.Contains(axis))
        {
            throw new InvalidOperationException($"This motion group has no {axis} axis.");
        }

        ValidatePositive(velocity, nameof(velocity));
        EnsureStopped();
        if (axis != MotionAxis.Z)
            EnsureZHomed();

        return await HomeAxesAsync([axis], velocity, cancellationToken);
    }

    public async Task<bool> HomeHorizontalAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        ValidatePositive(velocity, nameof(velocity));
        EnsureStopped();
        EnsureZHomed();
        MotionAxis[] axes = HasY ? [MotionAxis.X, MotionAxis.Y] : [MotionAxis.X];
        return await HomeAxesAsync(axes, velocity, cancellationToken);
    }

    protected abstract void ResetAlarm();

    public abstract void Stop();

    public void Reset()
    {
        ResetAlarm();
        foreach (var axis in Axes)
        {
            SetServo(axis, true);
        }
    }

    protected abstract Task MoveXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task MoveAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task<bool> HomeAxesAsync(
        MotionAxis[] axes,
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

    protected void BeginMotion(bool horizontal, bool adjustment = false)
    {
        if (adjustment)
            _command = MotionCommand.Adjustment;
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

    private void EnsureZHomed()
    {
        if (HasZ && !GetAxisState(MotionAxis.Z).Homed)
            throw new MotionInterlockException("Home Z before homing X/Y.");
    }

    private void EnsureHorizontalZ()
    {
        if (!IsAtHorizontalZ)
        {
            throw new MotionInterlockException(
                $"Horizontal movement requires homed Z at {HorizontalZ:F3}.");
        }
    }

    private void EnsureHasY()
    {
        if (!HasY)
        {
            throw new InvalidOperationException("This motion group has no Y axis.");
        }
    }

    private void EnsureHasZ()
    {
        if (!HasZ)
        {
            throw new InvalidOperationException("This motion group has no Z axis.");
        }
    }

    private void EnsureStopped()
    {
        if (Volatile.Read(ref _activeMotions) > 0
            || IsMoving
            || Axes.Any(axis => !GetAxisState(axis).InPosition))
        {
            throw new MotionInterlockException("Wait for all axes to stop and confirm InPosition before moving.");
        }
    }

    protected static void ValidatePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be positive and finite.");
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
