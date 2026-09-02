using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantry
{
    private readonly IXyMotion _motion;

    public InspectionGantry(IXyMotion motion)
    {
        _motion = motion;
        Motion = new(motion);
    }

    public MotionStatus Motion { get; }
    public IMotionFeedback Feedback => _motion;

    public void InitializeMotion() => _motion.Initialize();

    public void ResetMotion() => _motion.Reset();

    public void SetServo(MotionAxis axis, bool on) =>
        _motion.SetServo(axis, on);

    public Task<bool> HomeAxisAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default) =>
        _motion.HomeAsync(axis, velocity, cancellationToken);

    public Task<bool> HomeHorizontalAsync(
        double velocity,
        CancellationToken cancellationToken = default) =>
        _motion.HomeHorizontalAsync(velocity, cancellationToken);

    public Task MoveToAsync(
        AxisPosition position,
        double velocity,
        CancellationToken cancellationToken = default) =>
        _motion.MoveToXYAsync(
            position.X,
            position.Y,
            velocity,
            cancellationToken);

    public void Jog(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
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
}
