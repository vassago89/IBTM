using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed partial class InspectionStation
{
    public MotionStatus Motion => _work.Motion;

    public IMotionFeedback Feedback => _motion;

    public void InitializeMotion()
    {
        _motion.Initialize();
    }

    public void StopMotion()
    {
        _motion.Stop();
    }

    public Task ResetMotionAsync(CancellationToken cancellationToken = default)
    {
        return _motion.ResetAsync(cancellationToken);
    }

    public void SetServo(MotionAxis axis, bool on)
    {
        _motion.SetServo(axis, on);
    }

    public async Task<bool> HomeAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Link(cancellationToken);
        EnsureCanMove(operation.Token);
        return await _motion.HomeAsync(axis, _motionSettings.Home(axis).SearchSpeed, operation.Token);
    }

    public async Task<bool> HomeHorizontalAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Link(cancellationToken);
        EnsureCanMove(operation.Token);
        return await _motion.HomeHorizontalAsync(_motionSettings.HorizontalHome.SearchSpeed, operation.Token);
    }

    public Task MoveToAsync(
        AxisPosition position,
        double? velocity = null,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return _motion.MoveToXYAsync(
            position.X, position.Y, velocity ?? _motionSettings.HorizontalSpeed, cancellationToken);
    }

    public Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return _motion.MoveAxisAsync(axis, position, velocity, cancellationToken);
    }

    public Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return _motion.JogAsync(axis, velocity, cancellationToken);
    }

    public bool IsAt(AxisPosition position, bool live = true)
    {
        return _work.IsAt(position, live);
    }

    private void EnsureCanMove(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRaised)
        {
            throw new MotionInterlockException("NG carrier pickup must be raised before inspection XY movement.");
        }
    }

}
