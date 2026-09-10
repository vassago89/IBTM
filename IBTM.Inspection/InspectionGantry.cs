using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantry
{
    private readonly IXyMotion _motion;
    private readonly NgCarrierTransfer _transfer;
    private readonly OperationCancellation _operations;
    private readonly MotionSettings _settings;

    public InspectionGantry(
        IXyMotion motion,
        NgCarrierTransfer transfer,
        OperationCancellation operations,
        InspectionGantrySettings settings)
    {
        _motion = motion;
        _transfer = transfer;
        _operations = operations;
        _settings = settings.Motion;
        Motion = new(motion);
    }

    public MotionStatus Motion { get; }

    public IMotionFeedback Feedback
    {
        get
        {
            return _motion;
        }
    }

    public bool CanMove
    {
        get
        {
            return _transfer.IsRaised;
        }
    }

    public bool CanHome
    {
        get
        {
            return _transfer.IsClear;
        }
    }

    public void InitializeMotion()
    {
        _motion.Initialize();
    }

    public void ResetMotion()
    {
        _motion.Reset();
    }

    public void SetServo(MotionAxis axis, bool on)
    {
        _motion.SetServo(axis, on);
    }

    public async Task<bool> HomeAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Link(cancellationToken);
        EnsureCanHome(operation.Token);
        return await _motion.HomeAsync(axis, _settings.Home(axis).SearchSpeed, operation.Token);
    }

    public async Task<bool> HomeHorizontalAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Link(cancellationToken);
        EnsureCanHome(operation.Token);
        return await _motion.HomeHorizontalAsync(_settings.HorizontalHome.SearchSpeed, operation.Token);
    }

    public Task MoveToAsync(
        AxisPosition position,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return _motion.MoveToXYAsync(position.X, position.Y, velocity, cancellationToken);
    }

    public bool CanJog(MotionAxis axis)
    {
        return axis is MotionAxis.X or MotionAxis.Y && CanMove;
    }

    public Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return axis switch
        {
            MotionAxis.X => _motion.MoveXAsync(position, velocity, cancellationToken),
            MotionAxis.Y => _motion.MoveYAsync(position, velocity, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
    }

    public Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        if (axis is not (MotionAxis.X or MotionAxis.Y))
            throw new ArgumentOutOfRangeException(nameof(axis));
        return _motion.JogAsync(axis, velocity, cancellationToken);
    }

    public bool IsAt(AxisPosition position)
    {
        var current = _motion.GetPosition();
        return !_motion.IsMoving
            && _motion.GetAxisState(MotionAxis.X).InPosition
            && _motion.GetAxisState(MotionAxis.Y).InPosition
            && Math.Abs(current.X - position.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - position.Y) <= MotionService.PositionToleranceMillimeters;
    }

    private void EnsureCanMove(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanMove)
        {
            throw new InvalidOperationException("NG carrier pickup must be raised before inspection XY movement.");
        }
    }

    private void EnsureCanHome(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanHome)
        {
            throw new InvalidOperationException("Release the NG carrier and raise the pickup before homing the inspection XY axes.");
        }
    }
}
