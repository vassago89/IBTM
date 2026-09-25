using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

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

    (double X, double Y, double Z) Position { get; }
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
    Task<bool> HomeAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default);
    Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default);
    // Reset drive alarms only. The machine sequence owns servo enablement.
    Task ResetAsync(CancellationToken cancellationToken = default);
    void SetServo(MotionAxis axis, bool on);
}

public interface IXyMotion : IAxisMotion
{
    Task AdjustAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default);
    Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default);
    Task<bool> HomeHorizontalAsync(double velocity, CancellationToken cancellationToken = default);
}

public abstract class MotionService : IXyMotion
{
    public const double PositionToleranceMillimeters = 0.05;

    private int _activeMotions;
    private int _activeHorizontalMotions;
    private MotionCommand _command = MotionCommand.Positioning;

    protected MotionService(
        MotionSettings settings,
        OperationCancellation operationCancellation,
        bool hasY = true,
        bool hasZ = true)
    {
        Settings = settings;
        Operations = operationCancellation;
        HasY = hasY;
        HasZ = hasZ;
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
        EnsureStopped();
        await MoveAsync(axis, position, velocity, cancellationToken);
    }

    public async Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        ValidatePositive(velocity, nameof(velocity));
        EnsureHasY();
        ValidateTarget(MotionAxis.X, x);
        ValidateTarget(MotionAxis.Y, y);
        EnsureStopped();
        await MoveXYAsync(x, y, velocity, cancellationToken);
    }

    public abstract Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default);

    protected void ValidateJog(MotionAxis axis, double velocity)
    {
        if (axis is not (MotionAxis.X or MotionAxis.Y or MotionAxis.Z))
            throw new ArgumentOutOfRangeException(nameof(axis));
        ValidatePositive(Math.Abs(velocity), nameof(velocity));
        if (axis == MotionAxis.Y)
            EnsureHasY();
        if (axis == MotionAxis.Z)
            EnsureHasZ();
        EnsureStopped();
    }

    public abstract void SetServo(MotionAxis axis, bool on);

    public abstract (double X, double Y, double Z) Position { get; }

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
        MotionAxis[] axes = HasY ? [MotionAxis.X, MotionAxis.Y] : [MotionAxis.X];
        return await HomeAxesAsync(axes, velocity, cancellationToken);
    }

    protected abstract Task ResetAlarmAsync(CancellationToken cancellationToken);

    public abstract void Stop();

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        using var operation = Operations.Link(cancellationToken);
        cancellationToken = operation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        await ResetAlarmAsync(cancellationToken).ConfigureAwait(false);
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
            || IsMoving)
        {
            throw new MotionInterlockException("Wait for all axes to stop before moving.");
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
