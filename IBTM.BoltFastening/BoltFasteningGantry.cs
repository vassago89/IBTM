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
            InputIo.PickupHeadUp,
            InputIo.PickupHeadDown);
    public BoltCylinderState ShootingHeadPosition =>
        CylinderState(
            InputIo.ShootingHeadUp,
            InputIo.ShootingHeadDown);
    public bool CanMoveHorizontal =>
        PickupHeadPosition == BoltCylinderState.Up
        && ShootingHeadPosition == BoltCylinderState.Up;
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

    internal bool IsAt(BoltTarget bolt) =>
        IsAt(_settings.GetBoltPosition(bolt, _carrierReference));

    public bool HasReference(FasteningHead head)
    {
        var reference = _settings.GetHead(head);
        return CarrierCoordinates.IsDefined(
            reference.UpperLeftLocatingPin,
            reference.LowerRightLocatingPin);
    }

    internal bool AtPickupPosition => IsAt(_settings.PickupPosition);

    internal bool AtPickupXY => IsAtXY(_settings.PickupPosition);

    public TeachingOutput[] GetTeachingOutputs() =>
    [
        new(OutputIo.PickupHeadDown, HardwareArea.BoltFastening, SetPickupHeadDownAsync),
        new(OutputIo.ShootingHeadDown, HardwareArea.BoltFastening,
            (down, token) => SetHeadDownAsync(FasteningHead.Shooting, down, token)),
        new(OutputIo.PickupHeadVacuumPump, HardwareArea.BoltFastening,
            (on, token) => SetVacuumAsync(FasteningHead.Pickup, on, token)),
        new(OutputIo.ShootingHeadVacuumPump, HardwareArea.BoltFastening,
            (on, token) => SetVacuumAsync(FasteningHead.Shooting, on, token)),
        new(OutputIo.ShootBolt, HardwareArea.BoltFastening, SetManualShootingAsync, RequiresHandler: false, HoldToRun: true),
    ];

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

    public Task<bool> HomeZAsync(
        double velocity,
        CancellationToken cancellationToken = default) =>
        _motion.HomeAsync(MotionAxis.Z, velocity, cancellationToken);

    public Task<bool> HomeHorizontalAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return _motion.HomeHorizontalAsync(velocity, cancellationToken);
    }

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

    public Task MoveZAsync(
        double z,
        CancellationToken cancellationToken = default) =>
        _motion.MoveZAsync(
            z,
            _settings.Motion.ZSpeed,
            cancellationToken);

    public async Task MoveToPickupPositionAsync(CancellationToken cancellationToken = default)
    {
        await MoveToPickupXYAsync(cancellationToken);
        await SetPickupHeadDownAsync(true, cancellationToken);
        await MoveToPickupZAsync(cancellationToken);
    }

    public async Task ReturnFromPickupAsync(CancellationToken cancellationToken = default)
    {
        await MoveToSafeZAsync(cancellationToken);
        await SetPickupHeadDownAsync(false, cancellationToken);
    }

    public Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default) =>
        point switch
        {
            { Target: TeachingTarget.BoltPickup } => MoveToPickupPositionAsync(cancellationToken),
            { Mode: TeachMode.XYOnly } => MoveToXYAsync(position.X, position.Y, cancellationToken),
            { Mode: TeachMode.ZOnly } => MoveZAsync(position.Z, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(point)),
        };

    public bool CanJog(MotionAxis axis) => axis switch
    {
        MotionAxis.X => true,
        MotionAxis.Y => _motion.HasY,
        MotionAxis.Z => _motion.HasZ,
        _ => false,
    };

    public Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        var range = _motion.GetRange(axis)
            ?? throw new InvalidOperationException("Set the axis range before jogging.");
        return AdjustAxisAsync(
            axis,
            velocity < 0 ? range.Minimum : range.Maximum,
            Math.Abs(velocity),
            cancellationToken);
    }

    public Task AdjustAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default) =>
        _motion.AdjustAxisAsync(axis, position, velocity, cancellationToken);

    public async Task CheckReadyAsync(
        CancellationToken cancellationToken = default)
    {
        await _shootingHead.CheckReadyAsync(cancellationToken);
        await _pickupHead.CheckReadyAsync(cancellationToken);
    }

    internal Task MoveToBoltAsync(
        BoltTarget bolt,
        CancellationToken cancellationToken = default)
    {
        var position = _settings.GetBoltPosition(
            bolt,
            _carrierReference);
        return MoveToXYAsync(
            position.X,
            position.Y,
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

        await SetHeadDownAsync(head, false, cancellationToken);
    }

    internal Task SetHeadDownAsync(
        FasteningHead head,
        bool down,
        CancellationToken cancellationToken = default) => head switch
        {
            FasteningHead.Pickup => SetPickupHeadDownAsync(down, cancellationToken),
            FasteningHead.Shooting => _io.SetOutputAndWaitAsync(
                OutputIo.ShootingHeadDown,
                down,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };

    internal Task RaiseShootingHeadAsync(
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.ShootingHeadDown,
            false,
            cancellationToken);

    public Task RaiseCylindersAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(
            SetPickupHeadDownAsync(false, cancellationToken),
            RaiseShootingHeadAsync(cancellationToken));

    internal async Task MoveToPickupXYAsync(
        CancellationToken cancellationToken = default)
    {
        await RaiseCylindersAsync(cancellationToken);
        EnsureCanMoveHorizontal(cancellationToken);
        await _motion.MoveToXYAsync(
            _settings.PickupPosition.X,
            _settings.PickupPosition.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    internal Task MoveToPickupZAsync(
        CancellationToken cancellationToken = default) =>
        _motion.MoveZAsync(
            _settings.PickupPosition.Z,
            _settings.Motion.ZSpeed,
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
        _io.SetOutputAndWaitAsync(
            OutputIo.PickupHeadDown,
            down,
            cancellationToken);

    internal Task AdvanceShootingEscapeAsync(
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.ShootingEscapeForward,
            true,
            cancellationToken);

    internal async Task ShootBoltAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
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

    private async Task SetManualShootingAsync(bool on, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _io.SetOutput(OutputIo.ShootBolt, on);
            if (on) await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StopShooting();
        }
    }

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

    private void EnsureCanMoveHorizontal(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanMoveHorizontal)
        {
            throw new InvalidOperationException(
                "Raise both fastening heads before moving X/Y.");
        }
    }

}
