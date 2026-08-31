using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningStation
{
    private const double PositionTolerance = 0.05;

    private readonly IBoltHead _shootingHead;
    private readonly IBoltHead _pickupHead;
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly BoltFasteningSettings _settings;
    private readonly CarrierReferenceSettings _carrierReference;

    public BoltFasteningStation(
        IBoltHead shootingHead,
        IBoltHead pickupHead,
        IIoService io,
        IXyMotion motion,
        BoltFasteningSettings settings,
        CarrierReferenceSettings carrierReference)
    {
        _shootingHead = shootingHead;
        _pickupHead = pickupHead;
        _io = io;
        _motion = motion;
        _settings = settings;
        _carrierReference = carrierReference;
    }

    public bool PickupBoltLoaded =>
        _io.GetInput(InputIo.BoltHead1VacuumDetected);
    public bool ShootingBoltLoaded =>
        _io.GetInput(InputIo.BoltHead2VacuumDetected);
    public BoltCylinderState PickupHead =>
        CylinderState(
            InputIo.BoltTableUp,
            InputIo.BoltTableDown,
            InputIo.BoltHead1Up,
            InputIo.BoltHead1Down);
    public BoltCylinderState ShootingHead =>
        CylinderState(
            InputIo.BoltHead2Up,
            InputIo.BoltHead2Down);
    public BoltEscapeState ShootingEscape =>
        (_io.GetInput(InputIo.ShootingEscapeForward),
            _io.GetInput(InputIo.ShootingEscapeBackward)) switch
        {
            (true, false) => BoltEscapeState.Forward,
            (false, true) => BoltEscapeState.Backward,
            _ => BoltEscapeState.Between,
        };

    public BoltHeadState HeadState(FasteningHead head) =>
        GetHead(head).State;

    public bool AtSafeZ =>
        !_motion.IsMoving
        && _motion.GetAxisState(MotionAxis.Z).InPosition
        && _motion.IsAtHorizontalZ;

    public bool IsAt(BoltPoint bolt) =>
        IsAt(_settings.GetBoltPosition(bolt, _carrierReference));

    public bool HasReference(FasteningHead head)
    {
        var reference = _settings.GetHead(head);
        return CarrierCoordinates.IsDefined(
            reference.UpperLeftLocatingPin,
            reference.LowerRightLocatingPin);
    }

    public bool IsAtXY(BoltPoint bolt) =>
        IsAtXY(_settings.GetBoltPosition(bolt, _carrierReference));

    public bool AtPickupPosition => IsAt(_settings.PickupPosition);

    public bool AtPickupXY => IsAtXY(_settings.PickupPosition);

    public async Task CheckReadyAsync(
        CancellationToken cancellationToken = default)
    {
        await _shootingHead.CheckReadyAsync(cancellationToken);
        await _pickupHead.CheckReadyAsync(cancellationToken);
    }

    public async Task MoveToBoltAsync(
        BoltPoint bolt,
        CancellationToken cancellationToken = default)
    {
        var position = _settings.GetBoltPosition(
            bolt,
            _carrierReference);
        if (bolt.Head == FasteningHead.Shooting)
        {
            await _io.SetOutputAndWaitAsync(
                OutputIo.ShootingEscapeForward,
                false,
                cancellationToken);
        }

        await _motion.MoveToAsync(
            position.X,
            position.Y,
            position.Z,
            cancellationToken);
    }

    public Task<BoltResult> TightenHeadAsync(
        FasteningHead head,
        CancellationToken cancellationToken = default) =>
        GetHead(head).TightenAsync(cancellationToken);

    public Task SelectPresetAsync(
        FasteningHead head,
        ushort preset,
        CancellationToken cancellationToken = default) =>
        GetHead(head).SelectPresetAsync(preset, cancellationToken);

    public async Task FinishFasteningAsync(
        FasteningHead head,
        CancellationToken cancellationToken = default)
    {
        await SetVacuumAsync(
            head,
            false,
            cancellationToken);

        if (head == FasteningHead.Pickup)
        {
            await SetPickupHeadDownAsync(false, cancellationToken);
        }
    }

    public async Task SelectHeadAsync(
        FasteningHead head,
        CancellationToken cancellationToken = default)
    {
        await SetPickupHeadDownAsync(false, cancellationToken);
        await _io.SetOutputAndWaitAsync(
            OutputIo.BoltHead2Down,
            head == FasteningHead.Shooting,
            cancellationToken);
    }

    public Task RaiseShootingHeadAsync(
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.BoltHead2Down,
            false,
            cancellationToken);

    public Task MoveToPickupPositionAsync(
        CancellationToken cancellationToken = default) =>
        _motion.MoveToAsync(
            _settings.PickupPosition.X,
            _settings.PickupPosition.Y,
            _settings.PickupPosition.Z,
            cancellationToken);

    public Task PickUpBoltAsync(
        CancellationToken cancellationToken = default) =>
        SetVacuumAsync(
            FasteningHead.Pickup,
            true,
            cancellationToken);

    public Task SetPickupHeadDownAsync(
        bool down,
        CancellationToken cancellationToken = default) =>
        Task.WhenAll(
            _io.SetOutputAndWaitAsync(
                OutputIo.BoltTableDown,
                down,
                cancellationToken),
            _io.SetOutputAndWaitAsync(
                OutputIo.BoltHead1Down,
                down,
                cancellationToken));

    public async Task LoadShootingBoltAsync(
        CancellationToken cancellationToken = default)
    {
        await _io.SetOutputAndWaitAsync(
            OutputIo.ShootingEscapeForward,
            false,
            cancellationToken);
        await _io.WaitForInputAsync(
            InputIo.ShootingTubeBoltDetected,
            false,
            cancellationToken);
        _io.SetOutput(OutputIo.BoltHead2VacuumPump, true);
        await _io.SetOutputAndWaitAsync(
            OutputIo.ShootingEscapeForward,
            true,
            cancellationToken);
        _io.SetOutput(OutputIo.ShootBolt, true);
        try
        {
            await _io.WaitForInputAsync(
                InputIo.ShootingTubeBoltDetected,
                true,
                cancellationToken);
            await _io.WaitForInputAsync(
                InputIo.BoltHead2VacuumDetected,
                true,
                cancellationToken);
        }
        finally
        {
            _io.SetOutput(OutputIo.ShootBolt, false);
        }

        await _io.WaitForInputAsync(
            InputIo.ShootingTubeBoltDetected,
            false,
            cancellationToken);
        await _io.SetOutputAndWaitAsync(
            OutputIo.ShootingEscapeForward,
            false,
            cancellationToken);
    }

    public Task RetractShootingEscapeAsync(
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.ShootingEscapeForward,
            false,
            cancellationToken);

    public Task MoveToSafeZAsync(
        CancellationToken cancellationToken = default) =>
        _motion.MoveToHorizontalZAsync(cancellationToken);

    public void Stop() =>
        _io.SetOutput(OutputIo.ShootBolt, false);

    public void DiscardPendingResults()
    {
        _shootingHead.DiscardPendingResult();
        _pickupHead.DiscardPendingResult();
    }

    private IBoltHead GetHead(FasteningHead head) => head switch
    {
        FasteningHead.Shooting => _shootingHead,
        FasteningHead.Pickup => _pickupHead,
        _ => throw new ArgumentOutOfRangeException(nameof(head)),
    };

    private bool IsAt(AxisPos target)
    {
        var current = _motion.GetPosition();
        return HorizontalInPosition
            && _motion.GetAxisState(MotionAxis.Z).InPosition
            && Math.Abs(current.X - target.X) <= PositionTolerance
            && Math.Abs(current.Y - target.Y) <= PositionTolerance
            && Math.Abs(current.Z - target.Z) <= PositionTolerance;
    }

    private bool IsAtXY(AxisPos target)
    {
        var current = _motion.GetPosition();
        return HorizontalInPosition
            && Math.Abs(current.X - target.X) <= PositionTolerance
            && Math.Abs(current.Y - target.Y) <= PositionTolerance;
    }

    private bool HorizontalInPosition =>
        !_motion.IsMoving
        && _motion.GetAxisState(MotionAxis.X).InPosition
        && _motion.GetAxisState(MotionAxis.Y).InPosition;

    private BoltCylinderState CylinderState(
        InputIo firstUp,
        InputIo firstDown,
        InputIo secondUp,
        InputIo secondDown) =>
        (_io.GetInput(firstUp),
            _io.GetInput(firstDown),
            _io.GetInput(secondUp),
            _io.GetInput(secondDown)) switch
        {
            (true, false, true, false) => BoltCylinderState.Up,
            (false, true, false, true) => BoltCylinderState.Down,
            _ => BoltCylinderState.Between,
        };

    private BoltCylinderState CylinderState(
        InputIo up,
        InputIo down) =>
        (_io.GetInput(up), _io.GetInput(down)) switch
        {
            (true, false) => BoltCylinderState.Up,
            (false, true) => BoltCylinderState.Down,
            _ => BoltCylinderState.Between,
        };

    private async Task SetVacuumAsync(
        FasteningHead head,
        bool on,
        CancellationToken cancellationToken)
    {
        var output = head == FasteningHead.Pickup
            ? OutputIo.BoltHead1VacuumPump
            : OutputIo.BoltHead2VacuumPump;
        var input = head == FasteningHead.Pickup
            ? InputIo.BoltHead1VacuumDetected
            : InputIo.BoltHead2VacuumDetected;
        _io.SetOutput(output, on);
        await _io.WaitForInputAsync(input, on, cancellationToken);
    }

}
