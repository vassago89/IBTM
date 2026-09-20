using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.NgConveyor;

namespace IBTM.Inspection;

public enum NgTransferDestination
{
    [Description("Station 3")]
    Station,
    [Description("Shuttle")]
    Shuttle,
}

public enum NgTransferState
{
    [Description("Waiting")]
    Idle,

    [Description("Raise Station 3 Backup Plate and Lower Stopper")]
    StationNotReady,

    [Description("Raise the Shuttle")]
    ShuttleNotReady,

    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Waiting for Destination")]
    WaitingForDestination,

    [Description("Raising Pickup")]
    Raising,

    [Description("Opening Gripper")]
    Opening,

    [Description("Picking Carrier")]
    PickingCarrier,

    [Description("Securing Carrier Grip")]
    GrippingCarrier,

    [Description("Moving and Lowering Carrier at Destination")]
    PlacingCarrier,

    [Description("Waiting for Placed Carrier")]
    WaitingForPlacement,

    [Description("Transfer Complete")]
    Completed,

    [Description("Carrier Held at Destination")]
    HoldingAtDestination,
}

public enum NgTransferLiftState
{
    [Description("Up")]
    Up,

    [Description("Between")]
    Between,

    [Description("Down")]
    Down,
}

public enum NgTransferGripperState
{
    [Description("Open")]
    Open,

    [Description("Between")]
    Between,

    [Description("Closed")]
    Closed,
}

public sealed partial class NgCarrierTransfer : AutoUnit, INgCarrierTransferFeedback
{
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly OperationCancellation _operations;
    private readonly MotionSettings _motionSettings;
    private readonly NgCarrierTransferSettings _settings;
    private readonly NgShuttleFeedback _shuttle;
    private readonly UnitSettings _units;

    public NgCarrierTransfer(
        IIoService io,
        IXyMotion motion,
        OperationCancellation operations,
        InspectionGantrySettings motionSettings,
        NgCarrierTransferSettings settings,
        NgShuttleFeedback shuttle,
        UnitSettings units)
    {
        _io = io;
        _motion = motion;
        _operations = operations;
        _motionSettings = motionSettings.Motion;
        _settings = settings;
        _shuttle = shuttle;
        _units = units;
        Motion = new(motion);
        Station = ConveyorStation.CreateInspection(io);
        io.InputChanged += OnInputChanged;
        motion.StateChanged += NotifyChanged;
        Station.Changed += NotifyChanged;
        shuttle.Changed += NotifyChanged;
    }

    public override event Action? Changed;

    public ConveyorStation Station { get; }

    public bool IsEmptyRepeatAllowed => !_units.MainConveyor && !_units.Inspection
        && !_units.NgShuttle && !_units.NgConveyor;

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    // Presence near the pickup does not prove that its gripper holds the carrier.
    public bool CarrierDetected => _io.GetInput(InputIo.NgCarrierDetected);

    // Unfinished pickup/release ownership, not proof that material is present.
    public bool IsTransferPending
    {
        get;
        private set
        {
            if (field == value)
                return;
            field = value;
            Changed?.Invoke();
        }
    }

    public NgTransferLiftState Lift
    {
        get
        {
            switch ((_io.GetInput(InputIo.NgCarrierPickupUp), _io.GetInput(InputIo.NgCarrierPickupDown)))
            {
                case (true, false):
                    return NgTransferLiftState.Up;
                case (false, true):
                    return NgTransferLiftState.Down;
                default:
                    return NgTransferLiftState.Between;
            }
        }
    }

    public NgTransferGripperState Gripper
    {
        get
        {
            switch ((
                _io.GetInput(InputIo.NgCarrierGripperOpen),
                _io.GetInput(InputIo.NgCarrierGripperClosed)))
            {
                case (true, false):
                    return NgTransferGripperState.Open;
                case (false, true):
                    return NgTransferGripperState.Closed;
                default:
                    return NgTransferGripperState.Between;
            }
        }
    }

    public bool IsRaised => Lift == NgTransferLiftState.Up;

    public bool IsClear => IsRaised && !IsTransferPending;

    public Task SetLiftUpAsync(bool up, CancellationToken cancellationToken = default)
    {
        if (up && IsTransferPending && Gripper != NgTransferGripperState.Closed)
            throw new MotionInterlockException("Confirm the NG gripper is closed before raising the pending transfer.");
        return _io.SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, !up, cancellationToken);
    }

    public async Task SetGripperOpenAsync(bool open, CancellationToken cancellationToken = default)
    {
        await _io.SetOutputAndWaitAsync(OutputIo.NgCarrierGripperClose, !open, cancellationToken);
        if (open && Gripper == NgTransferGripperState.Open)
            IsTransferPending = false;
    }

    internal async Task GripForTransferAsync(CancellationToken cancellationToken, bool allowEmpty)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsTransferPending = true;
        await SetGripperOpenAsync(false, cancellationToken);
        if (!allowEmpty)
            await _io.WaitForInputAsync(InputIo.NgCarrierDetected, true, cancellationToken);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        // An unexpected Open is grip loss, not a completed release of the pending transfer.
        if (input is InputIo.NgCarrierPickupUp
            or InputIo.NgCarrierPickupDown
            or InputIo.NgCarrierGripperOpen
            or InputIo.NgCarrierGripperClosed
            or InputIo.NgCarrierDetected)
        {
            Changed?.Invoke();
        }
    }

    public MotionStatus Motion { get; }

    public IMotionFeedback Feedback => _motion;

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
        double velocity,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return _motion.MoveToXYAsync(position.X, position.Y, velocity, cancellationToken);
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
        var current = Motion.ReadPosition(live);
        return Motion.IsSettled(live, MotionAxis.X, MotionAxis.Y)
            && Math.Abs(current.X - position.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - position.Y) <= MotionService.PositionToleranceMillimeters;
    }

    private void EnsureCanMove(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRaised)
        {
            throw new MotionInterlockException("NG carrier pickup must be raised before inspection XY movement.");
        }
    }

    public bool IsCarrierPresent(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? Station.CarrierPresent
            : _shuttle.CarrierDetected;
    }

    public NgTransferState GetState(
        NgTransferDestination destination,
        bool canPickUp,
        bool canReceive = true,
        bool holdAtDestination = false,
        bool live = true,
        bool allowEmpty = false)
    {
        var source = GetOppositeDestination(destination);
        var destinationPosition = GetTransferPosition(destination);
        var sourcePosition = GetTransferPosition(source);
        var atDestination = destinationPosition is not null && IsAt(destinationPosition, live);
        var atSource = sourcePosition is not null && IsAt(sourcePosition, live);
        // Transfer-only repeat keeps the carrier gripped; the disabled shuttle is not a support.
        var holdAtShuttle = holdAtDestination && destination == NgTransferDestination.Shuttle;
        var destinationPresent = !holdAtShuttle && IsCarrierPresent(destination);
        var lift = Lift;
        var gripper = Gripper;
        var pending = IsTransferPending;
        var raised = lift == NgTransferLiftState.Up;
        var down = lift == NgTransferLiftState.Down;
        var open = gripper == NgTransferGripperState.Open;
        switch (true)
        {
            case true when holdAtDestination && atDestination && down && pending:
                switch (true)
                {
                    case true when !holdAtShuttle && !IsSupportReady(destination):
                        return GetSupportWaitingState(destination);
                    default:
                        return gripper == NgTransferGripperState.Closed
                            && (allowEmpty || CarrierDetected)
                            ? NgTransferState.HoldingAtDestination
                            : NgTransferState.GrippingCarrier;
                }
            case true when !holdAtShuttle && atDestination && open && !pending
                && (destinationPresent || allowEmpty):
                return raised ? NgTransferState.Completed : NgTransferState.Raising;
            case true when !holdAtShuttle
                && atDestination
                && open
                && !pending
                && !raised
                && !IsCarrierPresent(source):
                return NgTransferState.WaitingForPlacement;
            case true when atDestination && down && (destinationPresent || allowEmpty):
                return IsSupportReady(destination) ? NgTransferState.Opening : GetSupportWaitingState(destination);
            case true when pending:
                switch (true)
                {
                    case true when gripper != NgTransferGripperState.Closed
                        || !allowEmpty && !CarrierDetected:
                        return NgTransferState.GrippingCarrier;
                    case true when !atDestination && !raised:
                        return NgTransferState.Raising;
                    case true when !holdAtShuttle && !IsSupportReady(destination):
                        return GetSupportWaitingState(destination);
                    case true when atDestination && !raised:
                        return down ? NgTransferState.Opening : NgTransferState.PlacingCarrier;
                    case true when destinationPresent || !canReceive:
                        return NgTransferState.WaitingForDestination;
                    default:
                        return NgTransferState.PlacingCarrier;
                }
            case true when !raised && (!canPickUp || !atSource):
                return NgTransferState.Raising;
            case true when !canPickUp:
                return open ? NgTransferState.Idle : NgTransferState.Opening;
            case true when !allowEmpty && !IsCarrierPresent(source):
                return NgTransferState.WaitingForCarrier;
            case true when destinationPresent:
                return NgTransferState.WaitingForDestination;
            case true when !IsSupportReady(source):
                return GetSupportWaitingState(source);
            case true when !holdAtShuttle && !IsSupportReady(destination):
                return GetSupportWaitingState(destination);
            case true when atSource:
                return down ? NgTransferState.GrippingCarrier
                    : open ? NgTransferState.PickingCarrier : NgTransferState.Opening;
            default:
                return open ? NgTransferState.PickingCarrier : NgTransferState.Opening;
        }
    }

    public async Task RunToAsync(
        NgTransferDestination destination,
        CancellationToken cancellationToken,
        bool allowEmpty = false)
    {
        BeginRun();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = GetState(destination, canPickUp: true, allowEmpty: allowEmpty);
                if (state == NgTransferState.Completed)
                    break;
                if (!await ExecuteAsync(destination, state, cancellationToken,
                    allowEmpty: allowEmpty))
                    await WaitForChangeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            EndRun(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    // False means the caller can wait or perform inspection while the transfer is idle.
    public async Task<bool> ExecuteAsync(
        NgTransferDestination destination,
        NgTransferState state,
        CancellationToken cancellationToken,
        bool holdAtDestination = false,
        bool allowEmpty = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TraceStep(state, destination.ToString());
        switch (state)
        {
            case NgTransferState.Raising:
                await SetLiftUpAsync(true, cancellationToken);
                break;
            case NgTransferState.Opening:
                if (IsTransferPending
                    && (Lift != NgTransferLiftState.Down
                        || !IsSupportReady(destination)
                        || !allowEmpty && !IsCarrierPresent(destination)
                        || GetTransferPosition(destination) is not { } releasePosition
                        || !IsAt(releasePosition)))
                    throw new InvalidOperationException("Confirm the destination supports the pending NG carrier before releasing it.");
                await SetGripperOpenAsync(true, cancellationToken);
                break;
            case NgTransferState.PickingCarrier:
                var source = GetOppositeDestination(destination);
                await MoveToCarrierAsync(source, cancellationToken);
                if (!IsSupportReady(source) || !allowEmpty && !IsCarrierPresent(source))
                    return false;
                await SetLiftUpAsync(false, cancellationToken);
                await GripForTransferAsync(cancellationToken, allowEmpty);
                await SetLiftUpAsync(true, cancellationToken);
                break;
            case NgTransferState.GrippingCarrier:
                var gripSource = GetOppositeDestination(destination);
                if (!IsSupportReady(gripSource)
                    || !allowEmpty && !IsCarrierPresent(gripSource)
                    || Lift != NgTransferLiftState.Down
                    || GetTransferPosition(gripSource) is not { } gripPosition
                    || !IsAt(gripPosition))
                {
                    if (IsTransferPending)
                        throw new InvalidOperationException("NG transfer grip is uncertain away from its supported pickup position. Check the carrier before resuming.");
                    return false;
                }
                await GripForTransferAsync(cancellationToken, allowEmpty);
                break;
            case NgTransferState.PlacingCarrier:
            {
                var position = GetTransferPosition(destination)
                    ?? throw new InvalidOperationException("Record Carrier Pickup (S3) X/Y before returning to Station 3.");
                using var carrying = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                void CheckGrip()
                {
                    if (!IsTransferPending || Gripper != NgTransferGripperState.Closed
                        || !allowEmpty && !CarrierDetected)
                        carrying.Cancel();
                }
                Changed += CheckGrip;
                try
                {
                    CheckGrip();
                    carrying.Token.ThrowIfCancellationRequested();
                    if (!IsAt(position))
                        await MoveToAsync(position, _settings.Speed, carrying.Token);
                    // The support may change while XY is moving; do not lower onto it blindly.
                    if (!IsSupportReady(destination)
                        && !(holdAtDestination && destination == NgTransferDestination.Shuttle))
                        return false;
                    CheckGrip();
                    carrying.Token.ThrowIfCancellationRequested();
                    await SetLiftUpAsync(false, carrying.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("NG transfer lost confirmed grip while carrying or lowering the carrier.");
                }
                finally
                {
                    Changed -= CheckGrip;
                }
                break;
            }
            case NgTransferState.WaitingForPlacement:
                if (destination == NgTransferDestination.Shuttle)
                    await _io.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, true, cancellationToken);
                else
                    await Station.WaitForCarrierAsync(cancellationToken);
                break;
            default:
                return false;
        }
        return true;
    }

    private static NgTransferDestination GetOppositeDestination(NgTransferDestination destination)
    {
        return destination == NgTransferDestination.Shuttle
            ? NgTransferDestination.Station
            : NgTransferDestination.Shuttle;
    }

    public async Task MoveToCarrierAsync(
        NgTransferDestination source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var position = GetTransferPosition(source)
            ?? throw new InvalidOperationException("Record Carrier Pickup (S3) X/Y before moving to a carrier.");
        if (IsAt(position))
            return;
        await MoveToAsync(position, _settings.Speed, cancellationToken);
    }

    private AxisPosition? GetTransferPosition(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? _settings.GetCarrierPickupPosition()
            : _settings.ShuttlePlacePosition;
    }

    private bool IsSupportReady(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? Station.BackupPlate == StationCylinderState.Up
                && Station.Stopper == StationCylinderState.Down
            : _shuttle.Lift == NgShuttleLiftState.Up;
    }

    private static NgTransferState GetSupportWaitingState(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? NgTransferState.StationNotReady
            : NgTransferState.ShuttleNotReady;
    }
}
