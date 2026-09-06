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
    public bool CanHome => !_transfer.CarrierDetected;

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
        await PrepareHomeAsync(operation.Token);
        return await _motion.HomeAsync(axis, velocity, operation.Token);
    }

    public async Task<bool> HomeHorizontalAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Link(cancellationToken);
        await PrepareHomeAsync(operation.Token);
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

    public void Jog(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        if (axis == MotionAxis.X)
        {
            _motion.JogX(velocity, cancellationToken);
        }
        else if (axis == MotionAxis.Y)
        {
            _motion.JogY(velocity, cancellationToken);
        }
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

    private async Task PrepareHomeAsync(CancellationToken cancellationToken)
    {
        EnsureCarrierReleased(cancellationToken);
        await _transfer.SetGripperClosedAsync(false, cancellationToken);
        await _transfer.SetLiftDownAsync(false, cancellationToken);
        EnsureCarrierReleased(cancellationToken);
        EnsureCanMove(cancellationToken);
    }

    private void EnsureCarrierReleased(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanHome)
        {
            throw new InvalidOperationException(
                "Release the NG carrier before homing the inspection XY axes.");
        }
    }
}
