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

    public IMotionFeedback Feedback
    {
        get
        {
            return _motion;
        }
    }

    public PlacementCylinderState Lift
    {
        get
        {
            return GetCylinderState(InputIo.PcbPlacementHandlerUp, InputIo.PcbPlacementHandlerDown);
        }
    }

    public bool HandlerRaised
    {
        get
        {
            return Lift == PlacementCylinderState.Up;
        }
    }

    public PlacementCylinderState IpmLift
    {
        get
        {
            return GetCylinderState(InputIo.PcbPlacementIpmUp, InputIo.PcbPlacementIpmDown);
        }
    }

    public PlacementRotationState Rotation
    {
        get
        {
            return (
                _io.GetInput(InputIo.PcbPlacementHandlerUnrotated),
                _io.GetInput(InputIo.PcbPlacementHandlerRotated)) switch
            {
                (true, false) => PlacementRotationState.Unrotated,
                (false, true) => PlacementRotationState.Rotated,
                _ => PlacementRotationState.Between,
            };
        }
    }

    public PlacementGripperState IpmGripper
    {
        get
        {
            return (
                _io.GetInput(InputIo.PcbPlacementIpmGripperOpen),
                _io.GetInput(InputIo.PcbPlacementIpmGripperClosed)) switch
            {
                (true, false) => PlacementGripperState.Open,
                (false, true) => PlacementGripperState.Closed,
                _ => PlacementGripperState.Between,
            };
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

    public bool PcbSecured
    {
        get
        {
            return Pcb == PlacementPcbState.Secured;
        }
    }

    public bool VacuumDetected
    {
        get
        {
            return _io.GetInput(InputIo.PcbPlacementVacuumDetected);
        }
    }

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
        if (axis != MotionAxis.Z)
            EnsureCanMoveHorizontal(cancellationToken);
        return await _motion.HomeAsync(axis, _settings.Motion.Home(axis).SearchSpeed, cancellationToken);
    }

    public async Task<bool> HomeHorizontalAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
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
        if (axis is MotionAxis.X or MotionAxis.Y)
            EnsureCanMoveHorizontal(cancellationToken);
        var speed = axis == MotionAxis.Z ? _settings.Motion.ZSpeed : _settings.Motion.HorizontalSpeed;
        return _motion.MoveAxisAsync(axis, position, speed, cancellationToken);
    }

    public Task MoveToXYAsync(AxisPosition position, CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    public Task MoveToAsync(double x, double y, double z, CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return _motion.MoveToAsync(x, y, z, cancellationToken);
    }

    public Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default)
    {
        return point.Mode switch
        {
            TeachMode.ZOnly => MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken),
            TeachMode.XYOnly => MoveToXYAsync(position, cancellationToken),
            TeachMode.Full => MoveToAsync(position.X, position.Y, position.Z, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(point)),
        };
    }

    public bool CanJog(MotionAxis axis, bool live = true)
    {
        return axis switch
        {
            MotionAxis.X => HandlerRaised && IsAtHorizontalZ(live),
            MotionAxis.Y => _motion.HasY && HandlerRaised && IsAtHorizontalZ(live),
            MotionAxis.Z => _motion.HasZ,
            _ => false,
        };
    }

    public void EnsureCanJog(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (axis is MotionAxis.X or MotionAxis.Y)
            EnsureCanMoveHorizontal(cancellationToken);
    }

    public Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        EnsureCanJog(axis, cancellationToken);
        return _motion.JogAsync(axis, velocity, cancellationToken);
    }

    public Task SetLiftDownAsync(bool down, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerDown, down, cancellationToken);
    }

    public Task RaiseAsync(CancellationToken cancellationToken = default)
    {
        return Task.WhenAll(
            SetLiftDownAsync(false, cancellationToken),
            SetIpmLiftDownAsync(false, cancellationToken));
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

    private PlacementCylinderState GetCylinderState(InputIo upInput, InputIo downInput)
    {
        return (_io.GetInput(upInput), _io.GetInput(downInput)) switch
        {
            (true, false) => PlacementCylinderState.Up,
            (false, true) => PlacementCylinderState.Down,
            _ => PlacementCylinderState.Between,
        };
    }

    private void EnsureCanMoveHorizontal(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HandlerRaised)
        {
            throw new MotionInterlockException("Raise the placement handler before moving X/Y.");
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
