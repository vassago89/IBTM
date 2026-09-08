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

    public InspectionGantry(
        IXyMotion motion,
        NgCarrierTransfer transfer,
        OperationCancellation operations)
    {
        _motion = motion;
        _transfer = transfer;
        _operations = operations;
        Motion = new(motion);
    }

    public MotionStatus Motion { get; }
    public IMotionFeedback Feedback => _motion;
    public bool CanMove => _transfer.IsRaised;
    public bool CanHome => _transfer.IsClear;

    public void InitializeMotion() => _motion.Initialize();

    public void ResetMotion() => _motion.Reset();

    public void SetServo(MotionAxis axis, bool on) =>
        _motion.SetServo(axis, on);

    public async Task<bool> HomeAxisAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Link(cancellationToken);
        EnsureCanHome(operation.Token);
        return await _motion.HomeAsync(axis, velocity, operation.Token);
    }

    public async Task<bool> HomeHorizontalAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Link(cancellationToken);
        EnsureCanHome(operation.Token);
        return await _motion.HomeHorizontalAsync(velocity, operation.Token);
    }

    public Task MoveToAsync(
        AxisPosition position,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return _motion.MoveToXYAsync(
            position.X,
            position.Y,
            velocity,
            cancellationToken);
    }

    public Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return axis switch
        {
            MotionAxis.X => _motion.JogXAsync(velocity, cancellationToken),
            MotionAxis.Y => _motion.JogYAsync(velocity, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
    }

    public bool IsAt(AxisPosition position)
    {
        var current = _motion.GetPosition();
        return !_motion.IsMoving
            && _motion.GetAxisState(MotionAxis.X).InPosition
            && _motion.GetAxisState(MotionAxis.Y).InPosition
            && Math.Abs(current.X - position.X)
                <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - position.Y)
                <= MotionService.PositionToleranceMillimeters;
    }

    private void EnsureCanMove(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanMove)
        {
            throw new InvalidOperationException(
                "NG carrier pickup must be raised before inspection XY movement.");
        }
    }

    private void EnsureCanHome(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanHome)
        {
            throw new InvalidOperationException(
                "Release the NG carrier and raise the pickup before homing the inspection XY axes.");
        }
    }
}
