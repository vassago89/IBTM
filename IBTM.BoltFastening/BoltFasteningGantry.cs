using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningGantry
{
    private readonly IBoltHead _shootingHead;
    private readonly IBoltHead _pickupHead;
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly BoltFasteningSettings _settings;
    private readonly CarrierReferenceSettings _carrierReference;

    public BoltFasteningGantry(
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
        Motion = new(motion);
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

    public MotionStatus Motion { get; }
    public IMotionFeedback Feedback => _motion;

    public bool PickupBoltLoaded =>
        _io.GetInput(InputIo.PickupHeadVacuumDetected);
    public bool ShootingBoltLoaded =>
        _io.GetInput(InputIo.ShootingHeadVacuumDetected);
    internal bool ShootingTubeBoltDetected =>
        _io.GetInput(InputIo.ShootingTubeBoltDetected);
    public BoltCylinderState PickupHeadPosition =>
        CylinderState(
            InputIo.BoltTableUp,
            InputIo.BoltTableDown,
            InputIo.PickupHeadUp,
            InputIo.PickupHeadDown);
    public BoltCylinderState ShootingHeadPosition =>
        CylinderState(
            InputIo.ShootingHeadUp,
            InputIo.ShootingHeadDown);
    internal BoltEscapeState ShootingEscape =>
        (_io.GetInput(InputIo.ShootingEscapeForward),
            _io.GetInput(InputIo.ShootingEscapeBackward)) switch
        {
            (true, false) => BoltEscapeState.Forward,
            (false, true) => BoltEscapeState.Backward,
            _ => BoltEscapeState.Between,
        };

    internal BoltHeadState HeadState(FasteningHead head) =>
        GetHead(head).State;

    internal bool AtSafeZ =>
        !_motion.IsMoving
        && _motion.GetAxisState(MotionAxis.Z).InPosition
        && _motion.IsAtHorizontalZ;

    internal bool IsAt(BoltPoint bolt) =>
        IsAt(_settings.GetBoltPosition(bolt, _carrierReference));

    public bool HasReference(FasteningHead head)
    {
        var reference = _settings.GetHead(head);
        return CarrierCoordinates.IsDefined(
            reference.UpperLeftLocatingPin,
            reference.LowerRightLocatingPin);
    }

    internal bool IsAtXY(BoltPoint bolt) =>
        IsAtXY(_settings.GetBoltPosition(bolt, _carrierReference));

    internal bool AtPickupPosition => IsAt(_settings.PickupPosition);

    internal bool AtPickupXY => IsAtXY(_settings.PickupPosition);

    public void InitializeMotion() => _motion.Initialize();

    public void ResetMotion() => _motion.Reset();

    public void SetServo(MotionAxis axis, bool on) =>
        _motion.SetServo(axis, on);

    public Task<bool> HomeAxisAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default) =>
        _motion.HomeAsync(axis, velocity, cancellationToken);

    public Task<bool> HomeZAsync(
        double velocity,
        CancellationToken cancellationToken = default) =>
        _motion.HomeAsync(MotionAxis.Z, velocity, cancellationToken);

    public Task<bool> HomeHorizontalAsync(
        double velocity,
        CancellationToken cancellationToken = default) =>
        _motion.HomeHorizontalAsync(velocity, cancellationToken);

    public Task MoveToXYAsync(
        double x,
        double y,
        CancellationToken cancellationToken = default) =>
        _motion.MoveToXYAsync(
            x,
            y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);

    public Task MoveZAsync(
        double z,
        CancellationToken cancellationToken = default) =>
        _motion.MoveZAsync(
            z,
            _settings.Motion.ZSpeed,
            cancellationToken);

    public Task MoveToAsync(
        double x,
        double y,
        double z,
        CancellationToken cancellationToken = default) =>
        _motion.MoveToAsync(x, y, z, cancellationToken);

    public void Jog(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        switch (axis)
        {
            case MotionAxis.X:
                _motion.JogX(velocity, cancellationToken);
                break;
            case MotionAxis.Y:
                _motion.JogY(velocity, cancellationToken);
                break;
            case MotionAxis.Z:
                _motion.JogZ(velocity, cancellationToken);
                break;
        }
    }

    public async Task CheckReadyAsync(
        CancellationToken cancellationToken = default)
    {
        await _shootingHead.CheckReadyAsync(cancellationToken);
        await _pickupHead.CheckReadyAsync(cancellationToken);
    }

    internal async Task MoveToBoltAsync(
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

    internal Task<BoltResult> TightenHeadAsync(
        FasteningHead head,
        CancellationToken cancellationToken = default) =>
        GetHead(head).TightenAsync(cancellationToken);

    internal Task SelectPresetAsync(
        FasteningHead head,
        ushort preset,
        CancellationToken cancellationToken = default) =>
        GetHead(head).SelectPresetAsync(preset, cancellationToken);

    internal async Task FinishFasteningAsync(
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

    internal async Task SelectHeadAsync(
        FasteningHead head,
        CancellationToken cancellationToken = default)
    {
        await SetPickupHeadDownAsync(false, cancellationToken);
        await _io.SetOutputAndWaitAsync(
            OutputIo.ShootingHeadDown,
            head == FasteningHead.Shooting,
            cancellationToken);
    }

    internal Task RaiseShootingHeadAsync(
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.ShootingHeadDown,
            false,
            cancellationToken);

    internal Task MoveToPickupPositionAsync(
        CancellationToken cancellationToken = default) =>
        _motion.MoveToAsync(
            _settings.PickupPosition.X,
            _settings.PickupPosition.Y,
            _settings.PickupPosition.Z,
            cancellationToken);

    internal Task PickUpBoltAsync(
        CancellationToken cancellationToken = default) =>
        SetVacuumAsync(
            FasteningHead.Pickup,
            true,
            cancellationToken);

    internal Task SetPickupHeadDownAsync(
        bool down,
        CancellationToken cancellationToken = default) =>
        Task.WhenAll(
            _io.SetOutputAndWaitAsync(
                OutputIo.BoltTableDown,
                down,
                cancellationToken),
            _io.SetOutputAndWaitAsync(
                OutputIo.PickupHeadDown,
                down,
                cancellationToken));

    internal async Task LoadShootingBoltAsync(
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
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
        await _io.SetOutputAndWaitAsync(
            OutputIo.ShootingEscapeForward,
            true,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var passage = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var boltPassed = _io.WaitForInputAsync(
            InputIo.ShootingTubeBoltDetected,
            true,
            passage.Token);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _io.SetOutput(OutputIo.ShootBolt, true);
            await boltPassed;
            await _io.WaitForInputAsync(
                InputIo.ShootingHeadVacuumDetected,
                true,
                cancellationToken);
        }
        finally
        {
            try
            {
                _io.SetOutput(OutputIo.ShootBolt, false);
            }
            finally
            {
                passage.Cancel();
                await boltPassed.ConfigureAwait(
                    ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    internal Task WaitForShootingTubeClearAsync(
        CancellationToken cancellationToken = default) =>
        _io.WaitForInputAsync(
            InputIo.ShootingTubeBoltDetected,
            false,
            cancellationToken);

    internal Task RetractShootingEscapeAsync(
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.ShootingEscapeForward,
            false,
            cancellationToken);

    public Task MoveToSafeZAsync(
        CancellationToken cancellationToken = default) =>
        _motion.MoveToHorizontalZAsync(cancellationToken);

    public void StopShooting() =>
        _io.SetOutput(OutputIo.ShootBolt, false);

    internal void DiscardPendingResults()
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

    private bool IsAt(AxisPosition target)
    {
        var current = _motion.GetPosition();
        return HorizontalInPosition
            && _motion.GetAxisState(MotionAxis.Z).InPosition
            && Math.Abs(current.X - target.X)
                <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - target.Y)
                <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Z - target.Z)
                <= MotionService.PositionToleranceMillimeters;
    }

    private bool IsAtXY(AxisPosition target)
    {
        var current = _motion.GetPosition();
        return HorizontalInPosition
            && Math.Abs(current.X - target.X)
                <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - target.Y)
                <= MotionService.PositionToleranceMillimeters;
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
            ? OutputIo.PickupHeadVacuumPump
            : OutputIo.ShootingHeadVacuumPump;
        var input = head == FasteningHead.Pickup
            ? InputIo.PickupHeadVacuumDetected
            : InputIo.ShootingHeadVacuumDetected;
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(output, on);
        await _io.WaitForInputAsync(input, on, cancellationToken);
    }

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.PickupHeadVacuumDetected
            or InputIo.ShootingHeadVacuumDetected
            or InputIo.ShootingTubeBoltDetected
            or InputIo.BoltTableUp
            or InputIo.BoltTableDown
            or InputIo.PickupHeadUp
            or InputIo.PickupHeadDown
            or InputIo.ShootingHeadUp
            or InputIo.ShootingHeadDown
            or InputIo.ShootingEscapeForward
            or InputIo.ShootingEscapeBackward)
        {
            Changed?.Invoke();
        }
    }

}
