using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.NgConveyor;
using IBTM.Storage;

namespace IBTM.Inspection;

public sealed partial class InspectionStation : AutoUnit
{
    private readonly InspectionWork _work;
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly OperationCancellation _operations;
    private readonly MotionSettings _motionSettings;
    private readonly NgCarrierTransferSettings _settings;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly UnitSettings _units;
    private HeatSinkSlot[]? _runTargets;
    // Current loop destinations for display only; never resume them after STOP.
    private HeatSinkSlot? _activePcb;
    private BoltPoint? _activeBolt;

    public InspectionStation(
        InspectionWork work,
        NgCarrierConveyor ngConveyor,
        OperationCancellation operations,
        InspectionGantrySettings motionSettings,
        NgCarrierTransferSettings settings,
        IIoService io,
        UnitSettings units,
        ICamera camera,
        ILightController light,
        LightingSettings lightingSettings,
        RecipeManager recipes)
    {
        _work = work;
        _io = io;
        _motion = work.Feedback;
        _operations = operations;
        _motionSettings = motionSettings.Motion;
        _settings = settings;
        _ngConveyor = ngConveyor;
        _units = units;
        _camera = camera;
        _light = light;
        _lightingSettings = lightingSettings;
        _recipes = recipes;
        _visionGate = new(1, 1);
        camera.LiveViewFailed += OnCameraLiveViewFailed;
        work.Changed += NotifyChanged;
        ngConveyor.Changed += NotifyChanged;
    }

    public override event Action? Changed;

    public InspectionStationState GetState(
        bool repeat = false,
        bool holdAtShuttle = false,
        bool live = true,
        bool? conveyorRunning = null,
        bool? mainConveyorRunning = null)
    {
        if (_work.CarrierSeatingRequested)
            return InspectionStationState.SeatingCarrier;
        if (_units.Inspection)
        {
            var transferState = GetTransferState(
                NgTransferDestination.Shuttle,
                canPickUp: repeat && IsEmptyRepeatAllowed && !Station.CarrierPresent
                    || Station.CarrierSeated && _work.Completed && (repeat || _work.RouteToNg),
                canReceive: holdAtShuttle || _ngConveyor.IsReceiveAllowed(conveyorRunning),
                holdAtDestination: holdAtShuttle,
                live: live,
                allowEmpty: repeat && IsEmptyRepeatAllowed);
            if (transferState is not InspectionStationState.Waiting and not InspectionStationState.TransferCompleted)
                return transferState;
        }

        var enabled = _work.Enabled;
        if (!enabled || !_work.IsReadyToInspect(mainConveyorRunning))
        {
            var waiting = !enabled ? InspectionStationState.Disabled
                : _work.IsWaitingForConveyor
                    ? InspectionStationState.WaitingForConveyor
                    : InspectionStationState.Waiting;
            return WaitAtWaitingPosition(waiting, live);
        }
        var target = InspectionTarget;
        if (target.Pcb is null)
            return InspectionStationState.CompletingInspection;
        if (target.Bolt is null)
        {
            return HasBarcodeRegion(target.Pcb.Value)
                ? InspectionStationState.ReadingBarcode
                : WaitAtWaitingPosition(InspectionStationState.BarcodeTeachingRequired, live);
        }
        return HasRegion(target.Bolt)
            ? InspectionStationState.InspectingBolt
            : WaitAtWaitingPosition(InspectionStationState.FovTeachingRequired, live);
    }

    public BoltPoint? GetActiveBolt(bool? mainConveyorRunning = null)
    {
        return _work.Enabled
            && _work.IsReadyToInspect(mainConveyorRunning)
            ? InspectionTarget.Bolt
            : null;
    }

    public HeatSinkSlot? GetActivePcb(bool? mainConveyorRunning = null)
    {
        return _work.Enabled && _work.IsReadyToInspect(mainConveyorRunning)
            ? InspectionTarget.Pcb
            : null;
    }

    public async Task RunAsync(
        IReadOnlyList<BoltPoint> bolts,
        CancellationToken cancellationToken = default,
        bool repeat = false,
        bool holdAtShuttle = false)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        BeginRun();
        try
        {
            if (_work.Enabled)
                _work.Restart(_work.CurrentJob);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (repeat && _work.Enabled && !_units.MainConveyor
                    && (IsEmptyRepeatAllowed || _work.Station.CarrierPresent)
                    && _work.PickupClear)
                {
                    if (_work.Completed || IsEmptyRepeatAllowed && !_work.Station.CarrierPresent)
                    {
                        if (_work.Station.BackupPlate != StationCylinderState.Up
                            || _work.Station.Stopper != StationCylinderState.Down)
                        {
                            TraceStep(InspectionStationState.SeatingCarrier, workId: _work.CurrentJob.Id);
                            await SeatStationAsync(cancellationToken);
                        }
                    }
                    else if (!_work.AtInspectionPosition)
                    {
                        await _work.Station.PrepareToReceiveAsync(cancellationToken);
                    }
                }
                if (!_work.Enabled)
                {
                    var job = _work.CurrentJob;
                    if (_work.Station.CarrierSeated || _work.AtInspectionPosition)
                        _work.Complete(job);
                    if (!_work.CarrierSeatingRequested)
                    {
                        TraceStep(InspectionStationState.Disabled, workId: job.Id,
                            waitingFor: _work.Completed ? "carrier transfer" : "carrier at station");
                        await WaitForChangeAsync(cancellationToken);
                        continue;
                    }
                }
                await ExecuteAsync(bolts, repeat, holdAtShuttle, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _work.ClearCarrierSeatingRequest();
            EndRun(cancellationToken);
        }
    }

    private async Task ExecuteAsync(
        IReadOnlyList<BoltPoint> bolts,
        bool repeat,
        bool holdAtShuttle,
        CancellationToken cancellationToken)
    {
        var state = GetState(repeat, holdAtShuttle);
        switch (state)
        {
            case InspectionStationState.PreparingTransfer
                or InspectionStationState.PickingCarrier
                or InspectionStationState.PlacingCarrier
                or InspectionStationState.WaitingForDestination
                or InspectionStationState.HoldingAtDestination:
                if (!await ExecuteTransferAsync(
                    NgTransferDestination.Shuttle, state, cancellationToken, holdAtShuttle,
                    allowEmpty: repeat && IsEmptyRepeatAllowed))
                    await WaitForChangeAsync(cancellationToken);
                return;
        }

        TraceStep(state, workId: _work.CurrentJob.Id,
            waitingFor: state is InspectionStationState.Waiting or InspectionStationState.WaitingForConveyor
                ? "carrier, supports and clear pickup" : null);
        switch (state)
        {
            case InspectionStationState.SeatingCarrier:
                await SeatStationAsync(cancellationToken);
                _work.ClearCarrierSeatingRequest();
                return;
            case InspectionStationState.ReturningToWaitingPosition:
                await MoveToWaitingPositionAsync(cancellationToken);
                return;
            case InspectionStationState.Disabled
                or InspectionStationState.Waiting
                or InspectionStationState.WaitingForConveyor
                or InspectionStationState.BarcodeTeachingRequired
                or InspectionStationState.FovTeachingRequired:
                await WaitForChangeAsync(cancellationToken);
                return;
        }

        var job = _work.CurrentJob;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckWorkPosition()
        {
            if (!_work.IsReadyToInspect())
            {
                operation.Cancel();
            }
        }

        _work.Changed += CheckWorkPosition;
        try
        {
            CheckWorkPosition();
            operation.Token.ThrowIfCancellationRequested();
            var targets = Enum.GetValues<HeatSinkSlot>().Where(_work.Station.IsHeatSinkPresent).ToArray();
            foreach (var heatSink in targets)
            {
                if (!bolts.Any(bolt => bolt.HeatSink == heatSink))
                    throw new InvalidOperationException(
                        $"{heatSink.GetDescription()} has no taught bolts. Complete bolt teaching before inspection.");
            }
            _runTargets = targets;

            foreach (var pcb in targets)
            {
                operation.Token.ThrowIfCancellationRequested();
                _work.RequireCurrentJob(job);
                _activePcb = pcb;
                _activeBolt = null;
                NotifyChanged();
                await WaitForTeachingAsync(pcb, null, operation.Token);
                TraceStep(InspectionStationState.ReadingBarcode, $"{pcb.GetDescription()} / Data Matrix", job.Id);
                var assembly = _work.GetAssembly(job, pcb);
                var barcode = await ReadBarcodeAsync(pcb, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                _work.RequireCurrentJob(job);
                assembly.PcbBarcode = barcode.Barcode;
                assembly.RecordInspectionCapture(barcode);

                foreach (var bolt in bolts.Where(bolt => bolt.HeatSink == pcb).OrderBy(bolt => bolt.Number))
                {
                    operation.Token.ThrowIfCancellationRequested();
                    _work.RequireCurrentJob(job);
                    _activeBolt = bolt;
                    NotifyChanged();
                    await WaitForTeachingAsync(pcb, bolt, operation.Token);
                    TraceStep(InspectionStationState.InspectingBolt, $"{pcb.GetDescription()} / Bolt {bolt.Number}", job.Id);
                    var capture = await InspectAsync(bolt, operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    _work.RequireCurrentJob(job);
                    assembly.RecordBoltPresence(bolt.Number, capture.Success);
                    assembly.RecordInspectionCapture(capture);
                }
            }

            _activePcb = null;
            _activeBolt = null;
            NotifyChanged();
            TraceStep(InspectionStationState.CompletingInspection, workId: job.Id);
            await MoveToWaitingPositionAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            foreach (var heatSink in targets)
                _work.GetAssembly(job, heatSink).CompleteInspection();
            _work.Complete(job);
        }
        catch (OperationCanceledException) when (
            operation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _work.Changed -= CheckWorkPosition;
            _runTargets = null;
            _activePcb = null;
            _activeBolt = null;
            NotifyChanged();
        }
    }

    private async Task WaitForTeachingAsync(HeatSinkSlot pcb, BoltPoint? bolt, CancellationToken cancellationToken)
    {
        while (bolt is null ? !HasBarcodeRegion(pcb) : !HasRegion(bolt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = WaitAtWaitingPosition(bolt is null
                ? InspectionStationState.BarcodeTeachingRequired
                : InspectionStationState.FovTeachingRequired, live: true);
            TraceStep(state, workId: _work.CurrentJob.Id);
            if (state == InspectionStationState.ReturningToWaitingPosition)
                await MoveToWaitingPositionAsync(cancellationToken);
            else
                await WaitForChangeAsync(cancellationToken);
        }
    }

    private InspectionStationState WaitAtWaitingPosition(InspectionStationState waiting, bool live)
    {
        return _work.PickupClear && !_work.IsTransferAtWaitingPosition(live)
            ? InspectionStationState.ReturningToWaitingPosition
            : waiting;
    }

    private async Task MoveToWaitingPositionAsync(CancellationToken cancellationToken)
    {
        var position = _work.WaitingPosition
            ?? throw new InvalidOperationException("Record Inspection Waiting X/Y before moving to the inspection waiting position.");
        if (!IsAt(position))
            await MoveToAsync(position, cancellationToken: cancellationToken);
    }

    private (HeatSinkSlot? Pcb, BoltPoint? Bolt) InspectionTarget
    {
        get
        {
            if (_runTargets is not null)
                return (_activePcb, _activeBolt);
            foreach (var pcb in Enum.GetValues<HeatSinkSlot>())
            {
                if (_work.Station.IsHeatSinkPresent(pcb))
                    return (pcb, null);
            }
            return (null, null);
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    public ConveyorStation Station => _work.Station;

    public bool IsEmptyRepeatAllowed => !_units.MainConveyor && !_units.NgConveyor;

    public bool IsTransferPending => _work.IsTransferPending;

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

    public bool IsRaised => _work.IsRaised;

    public bool IsClear => _work.IsClear;

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
            _work.IsTransferPending = false;
    }

    public bool IsCarrierPresent(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? Station.CarrierPresent
            : _io.GetInput(InputIo.NgShuttleCarrierDetected);
    }

    public InspectionStationState GetTransferState(
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
        var destinationReady = holdAtShuttle || IsSupportReady(destination);
        // S3 support can be prepared after XY travel, with the pickup still raised.
        var canPrepareStation = destination == NgTransferDestination.Station && raised;

        if (atDestination)
        {
            if (holdAtDestination && down && pending)
            {
                if (!destinationReady)
                    return InspectionStationState.WaitingForDestination;
                return gripper == NgTransferGripperState.Closed
                    ? InspectionStationState.HoldingAtDestination : InspectionStationState.PickingCarrier;
            }

            if (!holdAtShuttle)
            {
                if (open && !pending && (destinationPresent || allowEmpty))
                    return raised ? InspectionStationState.TransferCompleted : InspectionStationState.PlacingCarrier;
                if (open && !pending && !raised && !IsCarrierPresent(source))
                    return InspectionStationState.PlacingCarrier;
                // Supported release can resume even while the gripper is between its sensors.
                if (down && (destinationPresent || allowEmpty))
                    return destinationReady ? InspectionStationState.PlacingCarrier : InspectionStationState.WaitingForDestination;
            }
        }

        if (pending)
        {
            if (gripper != NgTransferGripperState.Closed)
                return InspectionStationState.PickingCarrier;
            if (!atDestination && !raised)
                return InspectionStationState.PreparingTransfer;
            if (!destinationReady && !canPrepareStation)
                return InspectionStationState.WaitingForDestination;
            if (atDestination && !raised)
                return InspectionStationState.PlacingCarrier;
            return destinationPresent || !canReceive
                ? InspectionStationState.WaitingForDestination : InspectionStationState.PlacingCarrier;
        }

        if (!raised && (!canPickUp || !canReceive || !atSource))
            return InspectionStationState.PreparingTransfer;
        if (!canPickUp)
            return open ? InspectionStationState.Waiting : InspectionStationState.PreparingTransfer;
        if (!canReceive)
            return open ? InspectionStationState.WaitingForDestination : InspectionStationState.PreparingTransfer;
        if (!allowEmpty && !IsCarrierPresent(source))
            return InspectionStationState.Waiting;
        if (destinationPresent || !IsSupportReady(source) || !destinationReady && !canPrepareStation)
            return InspectionStationState.WaitingForDestination;
        return (atSource && down) || open
            ? InspectionStationState.PickingCarrier : InspectionStationState.PreparingTransfer;
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
                var state = GetTransferState(destination, canPickUp: true, allowEmpty: allowEmpty);
                if (state == InspectionStationState.TransferCompleted)
                    break;
                if (!await ExecuteTransferAsync(destination, state, cancellationToken,
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
    public async Task<bool> ExecuteTransferAsync(
        NgTransferDestination destination,
        InspectionStationState state,
        CancellationToken cancellationToken,
        bool holdAtDestination = false,
        bool allowEmpty = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TraceStep(state, destination.ToString(), waitingFor: state == InspectionStationState.WaitingForDestination
            ? $"source support={IsSupportReady(GetOppositeDestination(destination))}, "
                + $"destination support={IsSupportReady(destination)}, destination occupied={IsCarrierPresent(destination)}, "
                + $"shuttle={_ngConveyor.ShuttleLift}, receive={_ngConveyor.IsReceiveAllowed()}, "
                + $"pickup={Lift}, gripper={Gripper}, pending={IsTransferPending}"
            : null);
        switch (state)
        {
            case InspectionStationState.PreparingTransfer:
                if (!IsRaised)
                    await SetLiftUpAsync(true, cancellationToken);
                if (!IsTransferPending && Gripper != NgTransferGripperState.Open)
                    await SetGripperOpenAsync(true, cancellationToken);
                break;
            case InspectionStationState.PickingCarrier:
                var source = GetOppositeDestination(destination);
                if (IsTransferPending
                    && (!IsSupportReady(source)
                        || !allowEmpty && !IsCarrierPresent(source)
                        || Lift != NgTransferLiftState.Down
                        || GetTransferPosition(source) is not { } gripPosition
                        || !IsAt(gripPosition)))
                {
                    throw new InvalidOperationException("NG transfer grip is uncertain away from its supported pickup position. Check the carrier before resuming.");
                }
                await MoveToCarrierAsync(source, cancellationToken);
                if (!IsSupportReady(source) || !allowEmpty && !IsCarrierPresent(source))
                    return false;
                if (Lift != NgTransferLiftState.Down)
                    await SetLiftUpAsync(false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // Descent can outlive the source's support or carrier feedback.
                if (!IsSupportReady(source) || !allowEmpty && !IsCarrierPresent(source))
                    return false;
                _work.IsTransferPending = true;
                await SetGripperOpenAsync(false, cancellationToken);
                await SetLiftUpAsync(true, cancellationToken);
                break;
            case InspectionStationState.PlacingCarrier:
            {
                var position = GetTransferPosition(destination)
                    ?? throw new InvalidOperationException("Record Carrier Pickup (S3) X/Y before returning to Station 3.");
                var supported = IsAt(position) && Lift == NgTransferLiftState.Down
                    && IsSupportReady(destination) && (allowEmpty || IsCarrierPresent(destination));
                if (IsTransferPending && (!supported || holdAtDestination))
                {
                    using var carrying = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    void CheckGrip()
                    {
                        if (!IsTransferPending || Gripper != NgTransferGripperState.Closed)
                            carrying.Cancel();
                    }
                    Changed += CheckGrip;
                    try
                    {
                        CheckGrip();
                        carrying.Token.ThrowIfCancellationRequested();
                        if (destination == NgTransferDestination.Station && !IsSupportReady(destination))
                            await SeatStationAsync(carrying.Token);
                        else if (!IsAt(position))
                            await MoveToAsync(position, _settings.Speed, carrying.Token);
                        // Recheck the support after XY travel before lowering.
                        if (!IsSupportReady(destination)
                            && !(holdAtDestination && destination == NgTransferDestination.Shuttle))
                            return false;
                        CheckGrip();
                        carrying.Token.ThrowIfCancellationRequested();
                        if (Lift != NgTransferLiftState.Down)
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
                }

                if (holdAtDestination && IsTransferPending)
                    break;
                if (!IsAt(position) || !IsSupportReady(destination))
                    return false;
                if (IsTransferPending || Gripper != NgTransferGripperState.Open)
                {
                    if (Lift != NgTransferLiftState.Down || !allowEmpty && !IsCarrierPresent(destination))
                        throw new InvalidOperationException("Confirm the destination supports the pending NG carrier before releasing it.");
                    await SetGripperOpenAsync(true, cancellationToken);
                }
                if (!allowEmpty)
                {
                    if (destination == NgTransferDestination.Shuttle)
                        await _io.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, true, cancellationToken);
                    else
                        await Station.WaitForCarrierAsync(cancellationToken);
                }
                if (!IsRaised)
                    await SetLiftUpAsync(true, cancellationToken);
                break;
            }
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

    public async Task SeatStationAsync(CancellationToken cancellationToken)
    {
        var position = _settings.GetCarrierPickupPosition()
            ?? throw new InvalidOperationException("Record Carrier Pickup (S3) X/Y before raising the inspection backup plate.");
        // This awaited sequence owns XY until the plate finishes rising.
        // Do not infer permission to raise the plate from a coordinate comparison.
        await MoveToAsync(position, _settings.Speed, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await Station.SeatAsync(cancellationToken);
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
            : _io.GetInput(InputIo.NgShuttleUp) && !_io.GetInput(InputIo.NgShuttleDown);
    }


    public MotionStatus Motion => _work.Motion;

    public IMotionFeedback Feedback => _motion;

    public void InitializeMotion()
    {
        _motion.Initialize();
    }

    public void StopMotion()
    {
        _motion.Stop();
    }

    public Task ResetMotionAsync(CancellationToken cancellationToken = default)
    {
        return _motion.ResetAsync(cancellationToken);
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
        double? velocity = null,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return _motion.MoveToXYAsync(
            position.X, position.Y, velocity ?? _motionSettings.HorizontalSpeed, cancellationToken);
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
        return _work.IsAt(position, live);
    }

    private void EnsureCanMove(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRaised)
        {
            throw new MotionInterlockException("NG carrier pickup must be raised before inspection XY movement.");
        }
    }



    public async Task WaitForRepeatEndAsync(CancellationToken cancellationToken)
    {
        var changed = new AsyncAutoResetEvent();
        Changed += changed.Set;
        try
        {
            while (GetTransferState(NgTransferDestination.Shuttle,
                canPickUp: true, holdAtDestination: true,
                allowEmpty: IsEmptyRepeatAllowed) != InspectionStationState.HoldingAtDestination)
                await changed.WaitAsync(cancellationToken);
        }
        finally
        {
            Changed -= changed.Set;
        }
    }

    public async Task ReturnToStationAsync(CancellationToken cancellationToken)
    {
        await RunToAsync(NgTransferDestination.Station, cancellationToken,
            allowEmpty: IsEmptyRepeatAllowed);
    }

    public async Task ClearStationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var waitingPosition = _work.WaitingPosition
            ?? throw new InvalidOperationException("Record Inspection Waiting X/Y before moving to the inspection waiting position.");
        if ((!IsEmptyRepeatAllowed && !Station.CarrierPresent)
            || IsTransferPending
            || Gripper != NgTransferGripperState.Open
            || !IsRaised)
            throw new InvalidOperationException("Place the carrier on Station 3 and raise the open pickup before moving to the waiting position.");

        if (!IsAt(waitingPosition))
            await MoveToAsync(waitingPosition, _settings.Speed, cancellationToken);
    }
}
