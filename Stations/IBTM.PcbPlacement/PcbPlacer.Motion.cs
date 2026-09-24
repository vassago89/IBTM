using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.PcbPlacement;

public sealed partial class PcbPlacer
{
    public async Task<bool> HomeAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        return await _motion.HomeAsync(axis, _settings.Motion.Home(axis).SearchSpeed, cancellationToken);
    }

    public async Task<bool> HomeHorizontalAsync(CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        return await _motion.HomeHorizontalAsync(
            _settings.Motion.HorizontalHome.SearchSpeed,
            cancellationToken);
    }

    public bool IsAtXY(AxisPosition position, bool live = true)
    {
        if (!Motion.IsSettled(live, MotionAxis.X, MotionAxis.Y))
            return false;
        var current = Motion.ReadPosition(live);
        return Math.Abs(current.X - position.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - position.Y) <= MotionService.PositionToleranceMillimeters;
    }

    public bool IsAtZ(AxisPosition position, bool live = true)
    {
        return Motion.IsSettled(live, MotionAxis.Z)
            && Math.Abs(Motion.ReadPosition(live).Z - position.Z) <= MotionService.PositionToleranceMillimeters;
    }

    public Task MoveToHorizontalZAsync(CancellationToken cancellationToken = default)
    {
        return MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, cancellationToken);
    }

    public bool IsAtY(AxisPosition position, bool live = true)
    {
        return Motion.IsSettled(live, MotionAxis.Y)
            && Math.Abs(Motion.ReadPosition(live).Y - position.Y) <= MotionService.PositionToleranceMillimeters;
    }

    public Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        var speed = axis == MotionAxis.Z ? _settings.Motion.ZSpeed : _settings.Motion.HorizontalSpeed;
        if (axis == MotionAxis.Z && !_motion.GetAxisState(axis).Homed)
            throw new MotionInterlockException("Home Placement Z before moving to a taught height.");
        return _motion.MoveAxisAsync(axis, position, speed, cancellationToken);
    }

    public async Task MoveToXYAsync(AxisPosition position, CancellationToken cancellationToken = default)
    {
        await MoveToHorizontalZAsync(cancellationToken);
        EnsureHandlerRaised(cancellationToken);
        await _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    public async Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default)
    {
        switch (point.Mode)
        {
            case TeachMode.ZOnly:
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                break;
            case TeachMode.XYOnly:
                await MoveToXYAsync(position, cancellationToken);
                break;
            case TeachMode.Full when point.Target == TeachingTarget.PlacementHandoff:
                EnsureHandlerRaised(cancellationToken);
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                await MoveAxisAsync(MotionAxis.X, position.X, cancellationToken);
                await MoveAxisAsync(MotionAxis.Y, position.Y, cancellationToken);
                break;
            case TeachMode.Full when point.Target is TeachingTarget.HeatSink1PcbPlacement
                or TeachingTarget.HeatSink2PcbPlacement:
                await MoveToHorizontalZAsync(cancellationToken);
                await MoveAxisAsync(MotionAxis.Y, position.Y, cancellationToken);
                await MoveAxisAsync(MotionAxis.X, position.X, cancellationToken);
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                break;
            case TeachMode.Full:
                if (!double.IsFinite(position.Z))
                    throw new ArgumentOutOfRangeException(nameof(position), "Target Z must be finite before XY movement.");
                await MoveToHorizontalZAsync(cancellationToken);
                EnsureHandlerRaised(cancellationToken);
                await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, cancellationToken);
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    public Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (axis != MotionAxis.Z)
            EnsureHandlerRaised(cancellationToken);
        return _motion.JogAsync(axis, velocity, cancellationToken);
    }

    public Task AdjustAxisAsync(
        MotionAxis axis, double position, double velocity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (axis != MotionAxis.Z)
            EnsureHandlerRaised(cancellationToken);
        return _motion.AdjustAxisAsync(axis, position, velocity, cancellationToken);
    }

    public Task SetLiftDownAsync(bool down, CancellationToken cancellationToken = default)
    {
        if (down && _motion.IsMoving)
            throw new MotionInterlockException("Stop the placement axes before lowering the handler.");
        return _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerDown, down, cancellationToken);
    }

    public Task SetIpmLiftDownAsync(bool down, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, down, cancellationToken);
    }

    public async Task SetVacuumAsync(bool on, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.PcbPlacementVacuumEjector, on);
        await _io.WaitForInputAsync(InputIo.PcbPlacementVacuumDetected, on, cancellationToken);
    }

    public Task WaitForPcbAsync(CancellationToken cancellationToken = default)
    {
        return _io.WaitForInputAsync(InputIo.PcbPlacementPcbDetected, true, cancellationToken);
    }

    private void EnsureHandlerRaised(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HandlerRaised)
        {
            throw new MotionInterlockException("Raise the placement handler before moving any axis.");
        }
    }
}
