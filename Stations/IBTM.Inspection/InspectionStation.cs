using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.NgConveyor;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.Inspection;

public sealed record CarrierImage(AxisPosition Center, ImageFrame Frame);

public sealed partial class InspectionStation : AutoUnit, INgCarrierTransferFeedback
{
    private readonly ILogger<InspectionStation>? _log;
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly OperationCancellation _operations;
    private readonly MotionSettings _motionSettings;
    private readonly NgCarrierTransferSettings _settings;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly UnitSettings _units;
    private HeatSinkSlot[]? _runTargets;
    // An unfinished shuttle handoff must still wait for clearance after STOP.
    private bool _waitingForShuttleDown;
    // Selected work belongs only to this run; STOP starts again at the first point.
    private (HeatSinkSlot Pcb, BoltPoint? Bolt)[]? _runPoints;
    private int _pointIndex;
    private ConveyorStation.Job? _runJob;
    private CancellationTokenSource? _inspectionOperation;
    // Scheduling ownership for this job only; never a physical position or restart checkpoint.
    private volatile ConveyorStation.Job? _inspectionRequestedJob;
    private volatile ConveyorStation.Job? _carrierSeatingRequestedJob;
    private readonly ICamera _camera;
    private readonly ILightController _light;
    private readonly LightingSettings _lightingSettings;
    private readonly RecipeManager _recipes;
    private readonly SemaphoreSlim _visionGate;
    private int? _lightChannel;

    public InspectionStation(
        ConveyorStation station,
        IXyMotion motion,
        MotionStatus motionStatus,
        NgCarrierConveyor ngConveyor,
        OperationCancellation operations,
        InspectionGantrySettings motionSettings,
        NgCarrierTransferSettings settings,
        IIoService io,
        UnitSettings units,
        ICamera camera,
        ILightController light,
        LightingSettings lightingSettings,
        RecipeManager recipes,
        ILogger<InspectionStation>? log = null)
    {
        _log = log;
        Station = station;
        Motion = motionStatus;
        _io = io;
        _motion = motion;
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
        station.Changed += NotifyChanged;
        motion.StateChanged += NotifyChanged;
        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
        ngConveyor.AttachTransfer(this);
        ngConveyor.Changed += NotifyChanged;
        recipes.Changed += NotifyChanged;
        recipes.InspectionSettingsChanged += NotifyChanged;
    }

    public BoltPoint? ActiveBolt => InspectionTarget.Bolt;

    public HeatSinkSlot? ActivePcb => InspectionTarget.Pcb;

    private (HeatSinkSlot? Pcb, BoltPoint? Bolt) InspectionTarget
    {
        get
        {
            var index = _pointIndex;
            return _runPoints is { } points && index < points.Length ? points[index] : (null, null);
        }
    }

    public ConveyorStation Station { get; }

    public bool IsEmptyRepeatAllowed => !_units.MainConveyor && !_units.NgConveyor;

    public StationCylinderState Lift
    {
        get
        {
            switch ((_io.GetInput(InputIo.NgCarrierPickupUp), _io.GetInput(InputIo.NgCarrierPickupDown)))
            {
                case (true, false):
                    return StationCylinderState.Up;
                case (false, true):
                    return StationCylinderState.Down;
                default:
                    return StationCylinderState.Between;
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

    public bool IsCarrierPresent(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? Station.CarrierPresent
            : _io.GetInput(InputIo.NgShuttleCarrierDetected);
    }

    public MotionStatus Motion { get; }

    // Unfinished pickup/release ownership, not proof that material is present.
    public bool IsTransferPending
    {
        get;
        private set
        {
            if (field == value)
                return;
            field = value;
            NotifyChanged();
        }
    }

    public bool IsRaised => _io.GetInput(InputIo.NgCarrierPickupUp)
        && !_io.GetInput(InputIo.NgCarrierPickupDown);

    public bool IsClear => IsRaised && !IsTransferPending;

    public bool IsAtInspectionPosition
    {
        get
        {
            return Station.CarrierPresent
                && Station.BackupPlate == StationCylinderState.Down
                && Station.Stopper == StationCylinderState.Up
                && !_io.GetOutput(OutputIo.MainConveyorRun);
        }
    }

    public bool InspectionRequested => ReferenceEquals(_inspectionRequestedJob, Station.CurrentJob);

    public bool CarrierSeatingRequested => ReferenceEquals(_carrierSeatingRequestedJob, Station.CurrentJob);

    public bool IsTransferAllowed
    {
        get
        {
            return Station.Completed
                && (IsAtInspectionPosition || Station.CarrierSeated);
        }
    }

    public bool IsReceiveAllowed => !Station.CarrierPresent && !IsTransferPending;

    public bool RouteToNg => !_units.Inspection || HasNg;

    public bool HasNg
    {
        get
        {
            return Station.CarrierPresent
                && (Station.HasNg
                    || _units.Inspection && Station.Completed
                        && !Station.Assemblies.Any(assembly => assembly.InspectionResult != AssemblyResult.Pending));
        }
    }

    internal bool IsWaitingForConveyor => Station.CarrierPresent && !Station.Completed
        && _units.MainConveyor && !InspectionRequested;

    private bool IsReadyToInspect
    {
        get
        {
            return Station.CarrierPresent && !Station.Completed
                && (!_units.MainConveyor || InspectionRequested)
                && IsAtInspectionPosition && IsClear;
        }
    }

    public bool IsTransferAtWaitingPosition
    {
        get
        {
            if (!IsClear)
                return false;
            if (!_units.Inspection)
                return true;
            return _settings.WaitingPosition is { } position
                && MotionService.IsAt(_motion, position);
        }
    }

    public void RequestInspection(ConveyorStation.Job job)
    {
        Station.RequireCurrentJob(job);
        if (!IsAtInspectionPosition || !IsClear)
            throw new InvalidOperationException("Inspection requires a present carrier, plate DOWN, stopper UP, stopped belt and clear pickup.");
        _inspectionRequestedJob = job;
        NotifyChanged();
    }

    public void ClearInspectionRequest()
    {
        _inspectionRequestedJob = null;
        _carrierSeatingRequestedJob = null;
        NotifyChanged();
    }

    public void RequestCarrierSeating(ConveyorStation.Job job)
    {
        Station.RequireCurrentJob(job);
        if (CarrierSeatingRequested)
            return;
        _carrierSeatingRequestedJob = job;
        NotifyChanged();
    }

    public void ClearCarrierSeatingRequest()
    {
        _carrierSeatingRequestedJob = null;
        NotifyChanged();
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        // An unexpected Open is grip loss, not a completed release of the pending transfer.
        if (input is InputIo.NgCarrierPickupUp
            or InputIo.NgCarrierPickupDown
            or InputIo.NgCarrierGripperOpen
            or InputIo.NgCarrierGripperClosed
            or InputIo.NgShuttleUp
            or InputIo.NgShuttleDown
            or InputIo.NgShuttleCarrierDetected)
        {
            NotifyChanged();
        }
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        if (output == OutputIo.MainConveyorRun)
            NotifyChanged();
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default,
        bool repeat = false)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        BeginRun();
        try
        {
            if (_units.Inspection)
                Station.Restart(Station.CurrentJob);
            while (!cancellationToken.IsCancellationRequested)
            {
                var step = !_units.Inspection && !CarrierSeatingRequested
                    ? InspectionStationState.Disabled : GetNextStep(repeat);
                if (!await ExecuteStepAsync(step, repeat, cancellationToken))
                    await WaitForChangeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            ClearInspectionOperation();
            ClearCarrierSeatingRequest();
            EndRun(cancellationToken);
        }
    }

    internal InspectionStationState GetNextStep(bool repeat = false)
    {
        if (_waitingForShuttleDown && IsClear && Gripper == NgTransferGripperState.Open)
            return _ngConveyor.ShuttleLift == StationCylinderState.Down
                ? InspectionStationState.ReturningToWaitingPosition
                : InspectionStationState.WaitingForShuttleDown;
        if (CarrierSeatingRequested)
            return InspectionStationState.SeatingCarrier;
        if (repeat && _units.Inspection && !_units.MainConveyor
            && (IsEmptyRepeatAllowed || Station.CarrierPresent) && IsClear)
        {
            if (Station.Completed || IsEmptyRepeatAllowed && !Station.CarrierPresent)
            {
                if (Station.BackupPlate != StationCylinderState.Up
                    || Station.Stopper != StationCylinderState.Down)
                    return InspectionStationState.SeatingCarrier;
            }
            else if (Station.BackupPlate != StationCylinderState.Down
                || Station.Stopper != StationCylinderState.Up)
                return InspectionStationState.PreparingInspectionPosition;
        }
        if (_units.Inspection)
        {
            var transferState = GetNextTransferStep(
                NgTransferDestination.Shuttle,
                canPickUp: repeat && IsEmptyRepeatAllowed && !Station.CarrierPresent
                    || Station.CarrierSeated && Station.Completed && (repeat || RouteToNg),
                canReceive: repeat || _ngConveyor.IsReceiveAllowed,
                holdAtDestination: repeat,
                allowEmpty: repeat && IsEmptyRepeatAllowed);
            if (transferState is not InspectionStationState.Waiting and not InspectionStationState.TransferCompleted)
                return transferState;
        }

        var enabled = _units.Inspection;
        if (!enabled || !IsReadyToInspect)
        {
            var waiting = !enabled ? InspectionStationState.Disabled
                : IsWaitingForConveyor
                    ? InspectionStationState.WaitingForConveyor
                    : InspectionStationState.Waiting;
            return WaitAtWaitingPosition(waiting);
        }
        if (_runJob is null || _inspectionOperation?.IsCancellationRequested == true
            || !ReferenceEquals(_runJob, Station.CurrentJob))
            return InspectionStationState.PreparingInspection;
        var target = InspectionTarget;
        if (target.Pcb is null)
            return InspectionStationState.CompletingInspection;
        if (target.Bolt is null)
        {
            return HasBarcodeRegion(target.Pcb.Value)
                ? InspectionStationState.ReadingBarcode
                : WaitAtWaitingPosition(InspectionStationState.BarcodeTeachingRequired);
        }
        return HasRegion(target.Bolt)
            ? InspectionStationState.InspectingBolt
            : WaitAtWaitingPosition(InspectionStationState.FovTeachingRequired);
    }

    private async Task<bool> ExecuteStepAsync(
        InspectionStationState state,
        bool repeat,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (state)
        {
            case InspectionStationState.PreparingTransfer
                or InspectionStationState.PickingCarrier
                or InspectionStationState.PlacingCarrier
                or InspectionStationState.WaitingForDestination
                or InspectionStationState.HoldingAtDestination:
                return await ExecuteTransferAsync(
                    NgTransferDestination.Shuttle, state, cancellationToken, holdAtDestination: repeat,
                    allowEmpty: repeat && IsEmptyRepeatAllowed);
        }

        EnterStep(state, workId: Station.CurrentJob.Id,
            waitingFor: state == InspectionStationState.WaitingForShuttleDown
                ? $"shuttle Down; current={_ngConveyor.ShuttleLift}"
                : state is InspectionStationState.Waiting or InspectionStationState.WaitingForConveyor
                    ? "carrier, supports and clear pickup" : null);
        switch (state)
        {
            case InspectionStationState.PreparingInspectionPosition:
                await Station.PrepareToReceiveAsync(cancellationToken);
                return true;
            case InspectionStationState.PreparingInspection:
                ClearInspectionOperation();
                BoltPoint[] bolts;
                lock (_recipes.InspectionSync)
                    bolts = _recipes.Current.Pcb.BoltPoints.ToArray();
                _runTargets = Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent).ToArray();
                var points = new List<(HeatSinkSlot Pcb, BoltPoint? Bolt)>();
                foreach (var pcb in _runTargets)
                {
                    var pcbBolts = bolts.Where(bolt => bolt.HeatSink == pcb).OrderBy(bolt => bolt.Number).ToArray();
                    if (pcbBolts.Length == 0)
                        throw new InvalidOperationException(
                            $"{pcb.GetDescription()} has no taught bolts. Complete bolt teaching before inspection.");
                    points.Add((pcb, null));
                    foreach (var bolt in pcbBolts)
                        points.Add((pcb, bolt));
                }
                _runJob = Station.CurrentJob;
                _runPoints = points.ToArray();
                _inspectionOperation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Changed += CheckInspectionPosition;
                CheckInspectionPosition();
                NotifyChanged();
                return true;
            case InspectionStationState.SeatingCarrier:
                await SeatStationAsync(cancellationToken);
                ClearCarrierSeatingRequest();
                return true;
            case InspectionStationState.ReturningToWaitingPosition:
                await MoveToWaitingPositionAsync(cancellationToken);
                return true;
            case InspectionStationState.Disabled:
                if (Station.CarrierSeated || IsAtInspectionPosition)
                    Station.Complete(Station.CurrentJob);
                return false;
            case InspectionStationState.Waiting
                or InspectionStationState.WaitingForConveyor
                or InspectionStationState.WaitingForShuttleDown
                or InspectionStationState.BarcodeTeachingRequired
                or InspectionStationState.FovTeachingRequired:
                return false;
        }

        var operation = _inspectionOperation
            ?? throw new InvalidOperationException("No inspection work is selected.");
        var job = _runJob!;
        var token = operation.Token;
        try
        {
            CheckInspectionPosition();
            token.ThrowIfCancellationRequested();
            Station.RequireCurrentJob(job);
            if (state == InspectionStationState.CompletingInspection)
            {
                await MoveToWaitingPositionAsync(token);
                token.ThrowIfCancellationRequested();
                foreach (var heatSink in _runTargets!)
                    Station.GetAssembly(job, heatSink).CompleteInspection();
                Station.Complete(job);
                ClearInspectionOperation();
                return true;
            }

            var target = InspectionTarget;
            var pcb = target.Pcb ?? throw new InvalidOperationException("No inspection target is selected.");
            var assembly = Station.GetAssembly(job, pcb);
            switch (state)
            {
                case InspectionStationState.ReadingBarcode:
                    EnterStep(state, $"{pcb.GetDescription()} / Data Matrix", job.Id);
                    var barcode = await ReadBarcodeAsync(pcb, token);
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    assembly.PcbBarcode = barcode.Barcode;
                    assembly.RecordInspectionCapture(barcode);
                    break;
                case InspectionStationState.InspectingBolt:
                    var bolt = target.Bolt ?? throw new InvalidOperationException("No inspection bolt is selected.");
                    EnterStep(state, $"{pcb.GetDescription()} / Bolt {bolt.Number}", job.Id);
                    var capture = await InspectAsync(bolt, token);
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    assembly.RecordBoltPresence(bolt.Number, capture.Success);
                    assembly.RecordInspectionCapture(capture);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state));
            }
            _pointIndex++;
            NotifyChanged();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            ClearInspectionOperation();
        }
        return true;
    }

    private void CheckInspectionPosition()
    {
        if (_inspectionOperation is not { } operation)
            return;
        lock (operation)
        {
            if (ReferenceEquals(operation, _inspectionOperation)
                && (!IsReadyToInspect || !ReferenceEquals(_runJob, Station.CurrentJob)))
                operation.Cancel();
        }
    }

    private void ClearInspectionOperation()
    {
        Changed -= CheckInspectionPosition;
        if (_inspectionOperation is { } operation)
        {
            lock (operation)
            {
                _inspectionOperation = null;
                operation.Dispose();
            }
        }
        _runJob = null;
        _runTargets = null;
        _runPoints = null;
        _pointIndex = 0;
        NotifyChanged();
    }

    private InspectionStationState WaitAtWaitingPosition(InspectionStationState waiting)
    {
        return IsClear && !IsTransferAtWaitingPosition
            ? InspectionStationState.ReturningToWaitingPosition
            : waiting;
    }

    private async Task MoveToWaitingPositionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var position = _settings.WaitingPosition
            ?? throw new InvalidOperationException("Record Inspection Waiting X/Y before moving to the inspection waiting position.");
        if (!MotionService.IsAt(_motion, position))
            await MoveToAsync(position, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_waitingForShuttleDown)
        {
            _waitingForShuttleDown = false;
            NotifyChanged();
        }
    }

    internal InspectionStationState GetNextTransferStep(
        NgTransferDestination destination,
        bool canPickUp,
        bool canReceive = true,
        bool holdAtDestination = false,
        bool allowEmpty = false)
    {
        var source = GetOppositeDestination(destination);
        var destinationPosition = GetTransferPosition(destination);
        var sourcePosition = GetTransferPosition(source);
        var atDestination = destinationPosition is not null && MotionService.IsAt(_motion, destinationPosition);
        var atSource = sourcePosition is not null && MotionService.IsAt(_motion, sourcePosition);
        // Repeat turns around above the shuttle with the carrier still raised and gripped.
        var holdAtShuttle = holdAtDestination && destination == NgTransferDestination.Shuttle;
        var destinationPresent = !holdAtShuttle && IsCarrierPresent(destination);
        var lift = Lift;
        var gripper = Gripper;
        var pending = IsTransferPending;
        var raised = lift == StationCylinderState.Up;
        var down = lift == StationCylinderState.Down;
        var open = gripper == NgTransferGripperState.Open;
        var destinationReady = holdAtShuttle || IsSupportReady(destination);
        // S3 support can be prepared after XY travel, with the pickup still raised.
        var canPrepareStation = destination == NgTransferDestination.Station && raised;

        if (atDestination)
        {
            if (holdAtDestination && (holdAtShuttle ? raised : down) && pending)
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
            if (!raised && (!atDestination || holdAtShuttle))
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
                var state = GetNextTransferStep(destination, canPickUp: true, allowEmpty: allowEmpty);
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
    internal async Task<bool> ExecuteTransferAsync(
        NgTransferDestination destination,
        InspectionStationState state,
        CancellationToken cancellationToken,
        bool holdAtDestination = false,
        bool allowEmpty = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnterStep(state, destination.ToString(), waitingFor: state == InspectionStationState.WaitingForDestination
            ? $"source support={IsSupportReady(GetOppositeDestination(destination))}, "
                + $"destination support={IsSupportReady(destination)}, destination occupied={IsCarrierPresent(destination)}, "
                + $"shuttle={_ngConveyor.ShuttleLift}, receive={_ngConveyor.IsReceiveAllowed}, "
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
                        || Lift != StationCylinderState.Down
                        || GetTransferPosition(source) is not { } gripPosition
                        || !MotionService.IsAt(_motion, gripPosition)))
                {
                    throw new InvalidOperationException("NG transfer grip is uncertain away from its supported pickup position. Check the carrier before resuming.");
                }
                await MoveToCarrierAsync(source, cancellationToken);
                if (!IsSupportReady(source) || !allowEmpty && !IsCarrierPresent(source))
                    return false;
                if (Lift != StationCylinderState.Down)
                    await SetLiftUpAsync(false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // Descent can outlive the source's support or carrier feedback.
                if (!IsSupportReady(source) || !allowEmpty && !IsCarrierPresent(source))
                    return false;
                IsTransferPending = true;
                await SetGripperOpenAsync(false, cancellationToken);
                await SetLiftUpAsync(true, cancellationToken);
                break;
            case InspectionStationState.PlacingCarrier:
            {
                var position = GetTransferPosition(destination)
                    ?? throw new InvalidOperationException("Record Carrier Pickup (S3) X/Y before returning to Station 3.");
                var supported = MotionService.IsAt(_motion, position) && Lift == StationCylinderState.Down
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
                        else if (!MotionService.IsAt(_motion, position))
                            await MoveToAsync(position, cancellationToken: carrying.Token);
                        CheckGrip();
                        carrying.Token.ThrowIfCancellationRequested();
                        if (holdAtDestination && destination == NgTransferDestination.Shuttle)
                            return true;
                        // Recheck the support after XY travel before lowering.
                        if (!IsSupportReady(destination))
                            return false;
                        if (Lift != StationCylinderState.Down)
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
                if (!MotionService.IsAt(_motion, position) || !IsSupportReady(destination))
                    return false;
                if (IsTransferPending || Gripper != NgTransferGripperState.Open)
                {
                    if (Lift != StationCylinderState.Down || !allowEmpty && !IsCarrierPresent(destination))
                        throw new InvalidOperationException("Confirm the destination supports the pending NG carrier before releasing it.");
                    if (destination == NgTransferDestination.Shuttle)
                        _waitingForShuttleDown = true;
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

    public async Task SeatStationAsync(CancellationToken cancellationToken)
    {
        var position = _settings.CarrierPickupPosition
            ?? throw new InvalidOperationException("Record Carrier Pickup (S3) X/Y before raising the inspection backup plate.");
        // This awaited sequence owns XY until the plate finishes rising.
        // Do not infer permission to raise the plate from a coordinate comparison.
        await MoveToAsync(position, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await Station.SeatAsync(cancellationToken);
    }

    private AxisPosition? GetTransferPosition(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? _settings.CarrierPickupPosition
            : _settings.ShuttlePlacePosition;
    }

    private bool IsSupportReady(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? Station.BackupPlate == StationCylinderState.Up
                && Station.Stopper == StationCylinderState.Down
            : _io.GetInput(InputIo.NgShuttleUp) && !_io.GetInput(InputIo.NgShuttleDown);
    }

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

    public async Task MoveToCarrierAsync(
        NgTransferDestination source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var position = GetTransferPosition(source)
            ?? throw new InvalidOperationException("Record Carrier Pickup (S3) X/Y before moving to a carrier.");
        if (MotionService.IsAt(_motion, position))
            return;
        await MoveToAsync(position, cancellationToken: cancellationToken);
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

    private void EnsureCanMove(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRaised)
        {
            throw new MotionInterlockException("NG carrier pickup must be raised before inspection XY movement.");
        }
        if (_waitingForShuttleDown && _ngConveyor.ShuttleLift != StationCylinderState.Down)
        {
            throw new MotionInterlockException("Wait for the NG shuttle to finish lowering after carrier release before inspection XY movement.");
        }
    }

    public event Action? LiveViewChanged;

    public event Action<ImageFrame, HeatSinkSlot, int?>? InspectionCaptured;

    public event Action<ImageFrame>? FrameReady
    {
        add => _camera.FrameReady += value;
        remove => _camera.FrameReady -= value;
    }

    public bool IsLiveView => _camera.IsLiveView;

    public Exception? LiveViewError { get; private set; }

    public async Task InitializeVisionAsync(CancellationToken cancellationToken = default)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(InitializeVision, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _visionGate.Release();
        }
    }

    private void InitializeVision()
    {
        // Recovery does not require a successful OFF on a disconnected device.
        // Each driver first restores its connection; camera initialization leaves acquisition stopped.
        Exception? failure = null;
        try
        {
            _camera.Initialize();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            _light.Initialize();
            _light.TurnOffAll();
            _lightChannel = null;
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        PublishLiveView(failure);
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    public bool HasBarcodePosition(HeatSinkSlot pcb)
    {
        return _recipes.Current.CarrierImages.Count(fov => fov.IsBarcode && fov.HeatSink == pcb && fov.Center is not null) == 1;
    }

    public bool HasBarcodeRegion(HeatSinkSlot pcb)
    {
        var size = _camera.FrameSize;
        var fovs = _recipes.Current.CarrierImages.Where(fov => fov.IsBarcode && fov.HeatSink == pcb).ToArray();
        return fovs.Length == 1
            && fovs[0].Center is not null
            && fovs[0].Region is { } region
            && region.IsInside(size.Width, size.Height);
    }

    public CarrierImageTile GetBarcodeFov(HeatSinkSlot pcb)
    {
        var fov = _recipes.Current.CarrierImages.SingleOrDefault(item => item.IsBarcode && item.HeatSink == pcb);
        if (fov is null)
            throw new InvalidOperationException($"Record a position for {pcb.GetDescription()} Data Matrix.");
        return fov;
    }

    public Task MoveToBarcodeAsync(HeatSinkSlot pcb, CancellationToken cancellationToken = default)
    {
        return MoveToAsync(_recipes.Current.GetInspectionPosition(GetBarcodeFov(pcb)), cancellationToken: cancellationToken);
    }

    public async Task<ImageFrame> CaptureCurrentAsync(
        CancellationToken cancellationToken = default,
        bool keepLiveView = false,
        int? lightLevel = null)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frame = await CaptureWithLightAsync(lightLevel, cancellationToken, keepLiveView).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return frame;
        }
        finally
        {
            _visionGate.Release();
        }
    }

    internal async Task<InspectionCapture> ReadBarcodeAsync(HeatSinkSlot pcb, CancellationToken cancellationToken)
    {
        (AxisPosition Center, PixelRegion Region, DataMatrixInspectionRecipe Decoder, int Light) settings;
        lock (_recipes.InspectionSync)
        {
            if (!HasBarcodeRegion(pcb))
                throw new InvalidOperationException($"Teach a FOV and ROI for {pcb.GetDescription()} Data Matrix.");
            var fov = GetBarcodeFov(pcb);
            var decoder = _recipes.Current.BoltInspection.GetDataMatrix(pcb);
            settings = (_recipes.Current.GetInspectionPosition(fov), fov.Region!, decoder, decoder.LightLevel ?? _recipes.Current.BoltInspection.LightLevel);
        }
        await MoveToAsync(settings.Center, cancellationToken: cancellationToken);
        var image = await CaptureCurrentAsync(cancellationToken, lightLevel: settings.Light);
        var capturedAt = DateTimeOffset.Now;
        InspectionCaptured?.Invoke(image, pcb, null);
        var text = await Task.Run(() => DataMatrixReader.Read(image, settings.Region, settings.Decoder), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(null, capturedAt, image, settings.Region, !string.IsNullOrEmpty(text), Barcode: text);
    }

    public bool HasPosition(BoltPoint point)
    {
        return point.InspectionPosition is not null && _recipes.Current.CarrierImages.Count(fov =>
            !fov.IsBarcode
            && fov.BoltNumber == point.Number
            && fov.HeatSink == point.HeatSink) == 1;
    }

    public bool HasRegion(BoltPoint point)
    {
        var size = _camera.FrameSize;
        var fovs = _recipes.Current.CarrierImages.Where(fov =>
            !fov.IsBarcode
            && fov.BoltNumber == point.Number
            && fov.HeatSink == point.HeatSink).ToArray();
        return point.InspectionPosition is not null && fovs.Length == 1
            && fovs[0].Region is { } region
            && region.IsInside(size.Width, size.Height);
    }

    public CarrierImageTile GetFov(BoltPoint point)
    {
        var fov = _recipes.Current.CarrierImages.SingleOrDefault(fov =>
            !fov.IsBarcode
            && fov.BoltNumber == point.Number
            && fov.HeatSink == point.HeatSink);
        if (fov is null)
            throw new InvalidOperationException(
                $"Record a position for {point.HeatSink.GetDescription()} bolt {point.Number}.");
        return fov;
    }

    public Task MoveToBoltAsync(BoltPoint point, CancellationToken cancellationToken = default)
    {
        return MoveToAsync(_recipes.Current.GetInspectionPosition(GetFov(point)), cancellationToken: cancellationToken);
    }

    private async Task<ImageFrame> CaptureWithLightAsync(
        int? lightLevel, CancellationToken cancellationToken, bool keepLiveView = false)
    {
        keepLiveView = keepLiveView && _camera.IsLiveView;
        if (!keepLiveView)
            await Task.Run(StopLiveView, cancellationToken).ConfigureAwait(false);
        var channel = keepLiveView
            ? _lightChannel ?? _lightingSettings.InspectionChannel
            : _lightingSettings.InspectionChannel;
        Exception? failure = null;
        try
        {
            await Task.Run(() => TurnLightOn(channel, lightLevel), cancellationToken).ConfigureAwait(false);
            await Task.Delay(_lightingSettings.StabilizationDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            return await _camera.CaptureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (!keepLiveView)
                await Task.Run(() => TurnLightOff(channel, failure)).ConfigureAwait(false);
        }
    }

    internal async Task<InspectionCapture> InspectAsync(BoltPoint point, CancellationToken cancellationToken = default)
    {
        (AxisPosition Center, PixelRegion Region, int Light, int Threshold, double Minimum) settings;
        lock (_recipes.InspectionSync)
        {
            if (!HasRegion(point))
                throw new InvalidOperationException($"Teach a FOV and ROI for {point.HeatSink.GetDescription()} bolt {point.Number}.");
            var fov = GetFov(point);
            var defaults = _recipes.Current.BoltInspection;
            settings = (_recipes.Current.GetInspectionPosition(fov), fov.Region!, point.LightLevel ?? defaults.LightLevel,
                point.BrightnessThreshold ?? defaults.BrightnessThreshold,
                point.MinimumBrightRatio ?? defaults.MinimumBrightRatio);
        }
        await MoveToAsync(settings.Center, cancellationToken: cancellationToken).ConfigureAwait(false);
        var image = await CaptureCurrentAsync(cancellationToken, lightLevel: settings.Light).ConfigureAwait(false);
        var capturedAt = DateTimeOffset.Now;
        InspectionCaptured?.Invoke(image, point.HeatSink, point.Number);
        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ratio = BinaryChecker.Check(image, settings.Region, settings.Threshold).BrightRatio;
                cancellationToken.ThrowIfCancellationRequested();
                return new InspectionCapture(point.Number, capturedAt, image, settings.Region, ratio >= settings.Minimum,
                    BrightRatio: ratio, MinimumBrightRatio: settings.Minimum);
            },
            cancellationToken);
    }

    public async Task<CarrierImage> CaptureCarrierImageAsync(
        CancellationToken cancellationToken = default,
        int? lightLevel = null)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var image = await Task.Run(
                async () =>
                {
                    if (_motion.IsMoving
                        || !_motion.GetAxisState(MotionAxis.X).InPosition
                        || !_motion.GetAxisState(MotionAxis.Y).InPosition)
                        throw new InvalidOperationException("Stop jogging before adding a map image.");

                    var position = _motion.Position;
                    var center = new AxisPosition { X = position.X, Y = position.Y };
                    var frame = await CaptureWithLightAsync(lightLevel, cancellationToken, keepLiveView: true).ConfigureAwait(false);
                    if (!MotionService.IsAt(_motion, center))
                        throw new InvalidOperationException("The gantry moved during capture. Stop jogging and capture the map image again.");
                    return new CarrierImage(center, frame);
                },
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return image;
        }
        finally
        {
            _visionGate.Release();
        }
    }

    public async Task StartLiveViewAsync(CancellationToken cancellationToken = default, int? lightLevel = null)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => StartLiveView(lightLevel, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var cancelled = exception is OperationCanceledException
                && cancellationToken.IsCancellationRequested;
            PublishLiveView(cancelled ? null : exception);
            throw;
        }
        finally
        {
            _visionGate.Release();
        }
    }

    private void StartLiveView(int? lightLevel, CancellationToken cancellationToken)
    {
        StopLiveView();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TurnLightOn(_lightingSettings.InspectionChannel, lightLevel);
            cancellationToken.ThrowIfCancellationRequested();
            _camera.StartLiveView();
            cancellationToken.ThrowIfCancellationRequested();
            if (LiveViewError is { } failure)
                ExceptionDispatchInfo.Throw(failure);
            LiveViewChanged?.Invoke();
        }
        catch (Exception failure)
        {
            try
            {
                StopLiveView();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
            throw;
        }
    }

    public async Task ApplyLiveLightAsync(int? lightLevel, CancellationToken cancellationToken = default)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsLiveView && _lightChannel is { } channel)
                await Task.Run(() => TurnLightOn(channel, lightLevel), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _visionGate.Release();
        }
    }

    public async Task StopLiveViewAsync()
    {
        await _visionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(StopLiveView).ConfigureAwait(false);
        }
        finally
        {
            _visionGate.Release();
        }
    }

    private void StopLiveView()
    {
        Exception? failure = null;
        try
        {
            _camera.StopLiveView();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            if (_lightChannel is { } channel)
                TurnLightOff(channel, failure);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        PublishLiveView(failure);
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private void OnCameraLiveViewFailed(Exception failure)
    {
        // The driver has stopped acquisition. Finish cleanup on the receive thread;
        // the next camera operation joins that thread before touching the light.
        try
        {
            if (_lightChannel is { } channel)
                TurnLightOff(channel, failure);
        }
        catch (Exception cleanupFailure)
        {
            failure = cleanupFailure;
        }
        PublishLiveView(failure);
        _log?.LogError(failure, "Inspection live view failed.");
    }

    private void PublishLiveView(Exception? failure = null)
    {
        LiveViewError = failure;
        LiveViewChanged?.Invoke();
    }

    private void TurnLightOff(int channel, Exception? failure)
    {
        try
        {
            _light.TurnOff(channel);
            _lightChannel = null;
        }
        catch (Exception cleanupFailure) when (failure is not null)
        {
            throw new AggregateException(failure, cleanupFailure);
        }
    }

    private void TurnLightOn(int channel, int? lightLevel)
    {
        _light.Initialize();
        _lightChannel = channel;
        _light.SetLevel(channel, lightLevel ?? _recipes.Current.BoltInspection.LightLevel);
        _light.TurnOn(channel);
    }
}
