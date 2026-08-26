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

            return _io.GetInput(InputIo.PcbPlacementVacuumDetected)
                && Gripper == PlacementGripperState.Closed
                    ? PlacementPcbState.Secured
                    : PlacementPcbState.Detected;
        }
    }

    public bool PcbSecured => Pcb == PlacementPcbState.Secured;

    public bool IsWaitingAbove(AxisPos position)
    {
        var current = _motion.GetPosition();
        return !_motion.IsMoving
            && _motion.IsAtHorizontalZ
            && Rotation == PlacementRotationState.Rotated
            && Math.Abs(current.X - position.X) <= PositionTolerance
            && Math.Abs(current.Y - position.Y) <= PositionTolerance;
    }

    public async Task SecureAtBufferAsync(
        CancellationToken cancellationToken)
    {
        await SetHandlerDownAsync(false, cancellationToken);
        await _motion.MoveToHorizontalZAsync(cancellationToken);
        await SetRotatedAsync(false, cancellationToken);
        _io.SetOutput(OutputIo.PcbPlacementVacuumEjector, false);
        await _io.WaitForInputAsync(
            InputIo.PcbPlacementVacuumDetected,
            false,
            cancellationToken);
        await SetIpmGripperAsync(false, cancellationToken);
        await SetIpmDownAsync(true, cancellationToken);
        await _motion.MoveToAsync(
            _settings.BufferHandoffPosition.X,
            _settings.BufferHandoffPosition.Y,
            _settings.BufferHandoffPosition.Z,
            cancellationToken);
        await SetHandlerDownAsync(true, cancellationToken);
        await _io.WaitForInputAsync(
            InputIo.PcbPlacementPcbDetected,
            true,
            cancellationToken);
        _io.SetOutput(OutputIo.PcbPlacementVacuumEjector, true);
        await _io.WaitForInputAsync(
            InputIo.PcbPlacementVacuumDetected,
            true,
            cancellationToken);
        await SetIpmGripperAsync(true, cancellationToken);
    }

    public async Task ClearBufferAsync(
        CancellationToken cancellationToken = default)
    {
        await SetIpmDownAsync(false, cancellationToken);
        await SetHandlerDownAsync(false, cancellationToken);
        await _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    public async Task WaitAboveHousingAsync(
        AxisPos position,
        CancellationToken cancellationToken = default)
    {
        await _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
        await SetRotatedAsync(true, cancellationToken);
    }

    public async Task PlaceAsync(
        AxisPos position,
        CancellationToken cancellationToken = default)
    {
        await _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
        await _motion.MoveZAsync(
            position.Z,
            _settings.Motion.ZSpeed,
            cancellationToken);
        await SetHandlerDownAsync(true, cancellationToken);
        _io.SetOutput(OutputIo.PcbPlacementVacuumEjector, false);
        await _io.WaitForInputAsync(
            InputIo.PcbPlacementVacuumDetected,
            false,
            cancellationToken);
        await SetIpmGripperAsync(false, cancellationToken);
        await SetIpmDownAsync(false, cancellationToken);
        await SetIpmGripperAsync(true, cancellationToken);
        await SetIpmDownAsync(true, cancellationToken);
        await SetHandlerDownAsync(false, cancellationToken);
        await _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    private Task SetHandlerDownAsync(
        bool down,
        CancellationToken cancellationToken) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.PcbPlacementHandlerDown,
            down,
            cancellationToken);

    private Task SetIpmDownAsync(
        bool down,
        CancellationToken cancellationToken) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.PcbPlacementIpmDown,
            down,
            cancellationToken);

    private Task SetRotatedAsync(
        bool rotated,
        CancellationToken cancellationToken) =>
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
