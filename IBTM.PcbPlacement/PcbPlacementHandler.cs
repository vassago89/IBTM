using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandler : IBufferPlacementState
{
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
        Motion = new(motion);
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

    public MotionStatus Motion { get; }
    public IMotionFeedback Feedback => _motion;

    public PlacementCylinderState Lift => CylinderState(
        InputIo.PcbPlacementHandlerUp,
        InputIo.PcbPlacementHandlerDown);

    public PlacementCylinderState IpmLift => CylinderState(
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

    public PlacementGripperState IpmGripper =>
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
                && IpmGripper == PlacementGripperState.Closed
                    ? PlacementPcbState.Secured
                    : PlacementPcbState.Detected;
        }
    }

    public bool PcbSecured => Pcb == PlacementPcbState.Secured;
    public bool VacuumDetected =>
        _io.GetInput(InputIo.PcbPlacementVacuumDetected);
    public bool AtHorizontalZ => IsAtHorizontalZ(live: true);

    private bool IsAtHorizontalZ(bool live) => live ?
        !_motion.IsMoving
        && _motion.GetAxisState(MotionAxis.Z).InPosition
        && _motion.IsAtHorizontalZ
        : !Motion.IsMoving
        && Motion.Axes[MotionAxis.Z].State is { InPosition: true }
        && Motion.IsAtZ(_settings.BufferEntryZ);
    public bool CanMoveHorizontal =>
        Lift == PlacementCylinderState.Up;
    internal bool AtBufferXY =>
        IsAtXY(_settings.BufferHandoffPosition);
    internal bool AtBufferZ =>
        IsAtZ(_settings.BufferHandoffPosition);

    public void InitializeMotion() => _motion.Initialize();

    public void ResetMotion() => _motion.Reset();

    public void SetServo(MotionAxis axis, bool on) =>
        _motion.SetServo(axis, on);

    public Task<bool> HomeAxisAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        if (axis != MotionAxis.Z) EnsureCanMoveHorizontal(cancellationToken);
        return _motion.HomeAsync(axis, velocity, cancellationToken);
    }

    public Task<bool> HomeHorizontalAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return _motion.HomeHorizontalAsync(velocity, cancellationToken);
    }

    internal bool IsAtXY(AxisPosition position) =>
        !_motion.IsMoving
        && _motion.GetAxisState(MotionAxis.X).InPosition
        && _motion.GetAxisState(MotionAxis.Y).InPosition
        && IsAtXY(_motion.GetPosition(), position);

    internal bool IsAtZ(AxisPosition position) =>
        !_motion.IsMoving
        && _motion.GetAxisState(MotionAxis.Z).InPosition
        && Math.Abs(_motion.GetPosition().Z - position.Z)
            <= MotionService.PositionToleranceMillimeters;

    public Task MoveToHorizontalZAsync(
        CancellationToken cancellationToken = default) =>
        _motion.MoveToHorizontalZAsync(cancellationToken);

    internal Task MoveAboveBufferAsync(
        CancellationToken cancellationToken = default) =>
        MoveAboveAsync(
            _settings.BufferHandoffPosition,
            cancellationToken);

    internal Task LowerToBufferAsync(
        CancellationToken cancellationToken = default) =>
        LowerToAsync(
            _settings.BufferHandoffPosition,
            cancellationToken);

    internal Task MoveAboveAsync(
        AxisPosition position,
        CancellationToken cancellationToken = default) =>
        MoveToXYAsync(position.X, position.Y, cancellationToken);

    internal Task LowerToAsync(
        AxisPosition position,
        CancellationToken cancellationToken = default) =>
        _motion.MoveZAsync(
            position.Z,
            _settings.Motion.ZSpeed,
            cancellationToken);

    public Task MoveXAsync(
        double x,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return _motion.MoveXAsync(
            x,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    public Task MoveYAsync(
        double y,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return _motion.MoveYAsync(
            y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    public Task MoveZAsync(
        double z,
        CancellationToken cancellationToken = default) =>
        _motion.MoveZAsync(
            z,
            _settings.Motion.ZSpeed,
            cancellationToken);

    public Task MoveToXYAsync(
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return _motion.MoveToXYAsync(
            x,
            y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    public Task MoveToAsync(
        double x,
        double y,
        double z,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return _motion.MoveToAsync(x, y, z, cancellationToken);
    }

    public Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default) =>
        point.Mode switch
        {
            TeachMode.ZOnly => MoveZAsync(position.Z, cancellationToken),
            TeachMode.XYOnly => MoveToXYAsync(position.X, position.Y, cancellationToken),
            TeachMode.Full => MoveToAsync(position.X, position.Y, position.Z, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(point)),
        };

    public bool CanJog(MotionAxis axis, bool live = true) => axis switch
    {
        MotionAxis.X => CanMoveHorizontal && IsAtHorizontalZ(live),
        MotionAxis.Y => _motion.HasY && CanMoveHorizontal && IsAtHorizontalZ(live),
        MotionAxis.Z => _motion.HasZ,
        _ => false,
    };

    public Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        switch (axis)
        {
            case MotionAxis.X:
                EnsureCanMoveHorizontal(cancellationToken);
                return _motion.JogXAsync(velocity, cancellationToken);
            case MotionAxis.Y:
                EnsureCanMoveHorizontal(cancellationToken);
                return _motion.JogYAsync(velocity, cancellationToken);
            case MotionAxis.Z:
                return _motion.JogZAsync(velocity, cancellationToken);
            default:
                throw new ArgumentOutOfRangeException(nameof(axis));
        }
    }

    internal Task SetLiftDownAsync(
        bool down,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.PcbPlacementHandlerDown,
            down,
            cancellationToken);

    public TeachingOutput[] GetTeachingOutputs() =>
    [
        new(OutputIo.PcbPlacementHandlerDown, HardwareArea.PcbPlacementHandler, SetLiftDownAsync),
        new(OutputIo.PcbPlacementIpmDown, HardwareArea.PcbPlacementHandler, SetIpmLiftDownAsync),
        new(OutputIo.PcbPlacementIpmGripperClose, HardwareArea.PcbPlacementHandler, SetIpmGripperAsync),
        new(OutputIo.PcbPlacementVacuumEjector, HardwareArea.PcbPlacementHandler, SetVacuumAsync),
        new(OutputIo.PcbPlacementHandlerRotate, HardwareArea.PcbPlacementHandler, SetRotatedAsync,
            live => IsAtHorizontalZ(live) && CanMoveHorizontal),
    ];

    public Task RaiseAsync(CancellationToken cancellationToken = default) =>
        SetLiftDownAsync(false, cancellationToken);

    internal Task SetIpmLiftDownAsync(
        bool down,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.PcbPlacementIpmDown,
            down,
            cancellationToken);

    internal Task SetRotatedAsync(
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

    internal async Task SetVacuumAsync(
        bool on,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.PcbPlacementVacuumEjector, on);
        await _io.WaitForInputAsync(
            InputIo.PcbPlacementVacuumDetected,
            on,
            cancellationToken);
    }

    internal Task WaitForPcbAsync(
        CancellationToken cancellationToken = default) =>
        _io.WaitForInputAsync(
            InputIo.PcbPlacementPcbDetected,
            true,
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

    private void EnsureCanMoveHorizontal(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanMoveHorizontal)
        {
            throw new InvalidOperationException(
                "Raise the placement handler before moving X/Y.");
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

    private static bool IsAtXY(
        (double X, double Y, double Z) current,
        AxisPosition position) =>
        Math.Abs(current.X - position.X)
            <= MotionService.PositionToleranceMillimeters
        && Math.Abs(current.Y - position.Y)
            <= MotionService.PositionToleranceMillimeters;
}
