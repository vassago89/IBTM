using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandler : IBufferPlacementState
{
    private const double PositionTolerance = 0.05;

    private readonly IXyMotion _motion;
    private readonly IIoService _io;
    private readonly PcbPlacementHandlerSettings _settings;

    public PcbPlacementHandler(
        IXyMotion motion,
        IIoService io,
        PcbPlacementHandlerSettings settings)
    {
        _motion = motion;
        _io = io;
        _settings = settings;
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

    public PlacementCylinderState Handler => CylinderState(
        InputIo.PcbPlacementHandlerUp,
        InputIo.PcbPlacementHandlerDown);

    public PlacementCylinderState Ipm => CylinderState(
        InputIo.PcbPlacementIpmUp,
        InputIo.PcbPlacementIpmDown);

    public PlacementRotationState Rotation =>
        (_io.GetInput(InputIo.PcbPlacementHandlerUnrotated),
            _io.GetInput(InputIo.PcbPlacementHandlerRotated)) switch
        {
            (true, false) => PlacementRotationState.Unrotated,
            (false, true) => PlacementRotationState.Rotated,
            _ => PlacementRotationState.Between,
        };

    public PlacementGripperState Gripper =>
        (_io.GetInput(InputIo.PcbPlacementIpmGripperOpen),
            _io.GetInput(InputIo.PcbPlacementIpmGripperClosed)) switch
        {
            (true, false) => PlacementGripperState.Open,
            (false, true) => PlacementGripperState.Closed,
            _ => PlacementGripperState.Between,
        };

    public PlacementPcbState Pcb
    {
        get
        {
            if (!_io.GetInput(InputIo.PcbPlacementPcbDetected))
            {
                return PlacementPcbState.None;
            }

            return VacuumDetected
                && Gripper == PlacementGripperState.Closed
                    ? PlacementPcbState.Secured
                    : PlacementPcbState.Detected;
        }
    }

    public bool PcbSecured => Pcb == PlacementPcbState.Secured;
    public bool VacuumDetected =>
        _io.GetInput(InputIo.PcbPlacementVacuumDetected);
    public bool AtHorizontalZ =>
        !_motion.IsMoving
        && _motion.GetAxisState(MotionAxis.Z).InPosition
        && _motion.IsAtHorizontalZ;
    public bool AtBufferXY =>
        IsAtXY(_settings.BufferHandoffPosition);
    public bool AtBufferZ =>
        IsAtZ(_settings.BufferHandoffPosition);

    public bool IsAtXY(AxisPos position) =>
        !_motion.IsMoving
        && _motion.GetAxisState(MotionAxis.X).InPosition
        && _motion.GetAxisState(MotionAxis.Y).InPosition
        && IsAtXY(_motion.GetPosition(), position);

    public bool IsAtZ(AxisPos position) =>
        !_motion.IsMoving
        && _motion.GetAxisState(MotionAxis.Z).InPosition
        && Math.Abs(_motion.GetPosition().Z - position.Z)
            <= PositionTolerance;

    public Task MoveToHorizontalZAsync(
        CancellationToken cancellationToken = default) =>
        _motion.MoveToHorizontalZAsync(cancellationToken);

    public Task MoveAboveBufferAsync(
        CancellationToken cancellationToken = default) =>
        MoveAboveAsync(
            _settings.BufferHandoffPosition,
            cancellationToken);

    public Task LowerToBufferAsync(
        CancellationToken cancellationToken = default) =>
        LowerToAsync(
            _settings.BufferHandoffPosition,
            cancellationToken);

    public Task MoveAboveAsync(
        AxisPos position,
        CancellationToken cancellationToken = default) =>
        _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);

    public Task LowerToAsync(
        AxisPos position,
        CancellationToken cancellationToken = default) =>
        _motion.MoveZAsync(
            position.Z,
            _settings.Motion.ZSpeed,
            cancellationToken);

    public Task SetHandlerDownAsync(
        bool down,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.PcbPlacementHandlerDown,
            down,
            cancellationToken);

    public Task SetIpmDownAsync(
        bool down,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.PcbPlacementIpmDown,
            down,
            cancellationToken);

    public Task SetRotatedAsync(
        bool rotated,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.PcbPlacementHandlerRotate,
            rotated,
            cancellationToken);

    public Task SetIpmGripperAsync(
        bool closed,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.PcbPlacementIpmGripperClose,
            closed,
            cancellationToken);

    public async Task SetVacuumAsync(
        bool on,
        CancellationToken cancellationToken = default)
    {
        _io.SetOutput(OutputIo.PcbPlacementVacuumEjector, on);
        await _io.WaitForInputAsync(
            InputIo.PcbPlacementVacuumDetected,
            on,
            cancellationToken);
    }

    public Task WaitForPcbAsync(
        bool present,
        CancellationToken cancellationToken = default) =>
        _io.WaitForInputAsync(
            InputIo.PcbPlacementPcbDetected,
            present,
            cancellationToken);

    private PlacementCylinderState CylinderState(
        InputIo upInput,
        InputIo downInput) =>
        (_io.GetInput(upInput), _io.GetInput(downInput)) switch
        {
            (true, false) => PlacementCylinderState.Up,
            (false, true) => PlacementCylinderState.Down,
            _ => PlacementCylinderState.Between,
        };

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

    private static bool IsAtXY(
        (double X, double Y, double Z) current,
        AxisPos position) =>
        Math.Abs(current.X - position.X) <= PositionTolerance
        && Math.Abs(current.Y - position.Y) <= PositionTolerance;
}
