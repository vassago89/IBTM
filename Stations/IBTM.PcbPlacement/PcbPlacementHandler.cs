using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandler : IPcbHandoffReceiver
{
    private readonly IXyMotion _motion;
    private readonly IIoService _io;
    private readonly PcbPlacementHandlerSettings _settings;

    public PcbPlacementHandler(IXyMotion motion, IIoService io, PcbPlacementHandlerSettings settings)
    {
        _motion = motion;
        _io = io;
        _settings = settings;
        Motion = new(motion);
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

    public MotionStatus Motion { get; }

    public IMotionFeedback Feedback => _motion;

    public PlacementCylinderState Lift
    {
        get
        {
            switch ((_io.GetInput(InputIo.PcbPlacementHandlerUp), _io.GetInput(InputIo.PcbPlacementHandlerDown)))
            {
                case (true, false):
                    return PlacementCylinderState.Up;
                case (false, true):
                    return PlacementCylinderState.Down;
                default:
                    return PlacementCylinderState.Between;
            }
        }
    }

    public bool HandlerRaised => Lift == PlacementCylinderState.Up;

    public PlacementCylinderState IpmLift
    {
        get
        {
            switch ((_io.GetInput(InputIo.PcbPlacementIpmUp), _io.GetInput(InputIo.PcbPlacementIpmDown)))
            {
                case (true, false):
                    return PlacementCylinderState.Up;
                case (false, true):
                    return PlacementCylinderState.Down;
                default:
                    return PlacementCylinderState.Between;
            }
        }
    }

    public PlacementRotationState Rotation
    {
        get
        {
            switch ((
                _io.GetInput(InputIo.PcbPlacementHandlerUnrotated),
                _io.GetInput(InputIo.PcbPlacementHandlerRotated)))
            {
                case (true, false):
                    return PlacementRotationState.Unrotated;
                case (false, true):
                    return PlacementRotationState.Rotated;
                default:
                    return PlacementRotationState.Between;
            }
        }
    }

    public PlacementGripperState IpmGripper
    {
        get
        {
            switch ((
                _io.GetInput(InputIo.PcbPlacementIpmGripperOpen),
                _io.GetInput(InputIo.PcbPlacementIpmGripperClosed)))
            {
                case (true, false):
                    return PlacementGripperState.Open;
                case (false, true):
                    return PlacementGripperState.Closed;
                default:
                    return PlacementGripperState.Between;
            }
        }
    }

    public PlacementPcbState Pcb
    {
        get
        {
            if (!_io.GetInput(InputIo.PcbPlacementPcbDetected))
            {
                return PlacementPcbState.None;
            }

            return VacuumDetected && IpmGripper == PlacementGripperState.Closed
                ? PlacementPcbState.Secured
                : PlacementPcbState.Detected;
        }
    }

    public bool PcbSecured => Pcb == PlacementPcbState.Secured;

    public bool VacuumDetected => _io.GetInput(InputIo.PcbPlacementVacuumDetected);

    public bool IsAtHorizontalZ(bool live = true)
    {
        return live
            ? !_motion.IsMoving
                && _motion.GetAxisState(MotionAxis.Z).InPosition
                && _motion.IsAtHorizontalZ
            : !Motion.IsMoving
                && Motion.Axes[MotionAxis.Z].State is { InPosition: true }
                && Motion.IsAtZ(_settings.BufferHandoffPosition.Z);
    }

    public bool IsAtBufferXY(bool live = true)
    {
        return IsAtXY(_settings.BufferHandoffPosition, live);
    }

    public void InitializeMotion()
    {
        _motion.Initialize();
    }

    public void StopMotion()
    {
        _motion.Stop();
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

    public async Task MoveToHorizontalZAsync(CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        await _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    public Task MoveAboveBufferAsync(CancellationToken cancellationToken = default)
    {
        return MoveToXYAsync(_settings.BufferHandoffPosition, cancellationToken);
    }

    public Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        var speed = axis == MotionAxis.Z ? _settings.Motion.ZSpeed : _settings.Motion.HorizontalSpeed;
        return _motion.MoveAxisAsync(axis, position, speed, cancellationToken);
    }

    public Task MoveToXYAsync(AxisPosition position, CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        return _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    public Task MoveToAsync(double x, double y, double z, CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        return _motion.MoveToAsync(x, y, z, cancellationToken);
    }

    public Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default)
    {
        switch (point.Mode)
        {
            case TeachMode.ZOnly:
                return MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
            case TeachMode.XYOnly:
                return MoveToXYAsync(position, cancellationToken);
            case TeachMode.Full:
                return MoveToAsync(position.X, position.Y, position.Z, cancellationToken);
            default:
                throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    public Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        return _motion.JogAsync(axis, velocity, cancellationToken);
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

    public Task SetRotatedAsync(bool rotated, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HandlerRaised || !IsAtHorizontalZ())
        {
            throw new MotionInterlockException(
                "Placement rotation requires the handler lift Up and Z stopped at the travel height.");
        }

        return _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerRotate, rotated, cancellationToken);
    }

    public Task SetIpmGripperAsync(bool closed, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmGripperClose, closed, cancellationToken);
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

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.PcbPlacementHandlerDown
            or InputIo.PcbPlacementHandlerUp
            or InputIo.PcbPlacementHandlerRotated
            or InputIo.PcbPlacementHandlerUnrotated
            or InputIo.PcbPlacementIpmDown
            or InputIo.PcbPlacementIpmUp
            or InputIo.PcbPlacementPcbDetected
            or InputIo.PcbPlacementVacuumDetected
            or InputIo.PcbPlacementIpmGripperClosed
            or InputIo.PcbPlacementIpmGripperOpen)
        {
            Changed?.Invoke();
        }
    }
}
