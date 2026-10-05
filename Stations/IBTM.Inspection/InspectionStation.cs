using System;
using System.Collections.Generic;
using System.Diagnostics;
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

public sealed class InspectionStation : AutoUnit, INgCarrierTransferFeedback
{
    private readonly ILogger<InspectionStation>? _log;
    private readonly IXyMotion _motion;
    private readonly MotionSettings _motionSettings;
    private readonly NgCarrierTransferSettings _settings;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly UnitSettings _units;
    // An unfinished shuttle handoff must still wait for clearance after STOP.
    private bool _waitingForShuttleDown;
    // Selected work belongs only to this run; STOP starts again at the first point.
    private (HeatSinkSlot Pcb, BoltPoint? Bolt)[]? _runPoints;
    private int _pointIndex;
    private long _cycleStartedAt;
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
        InspectionGantrySettings motionSettings,
        NgCarrierTransferSettings settings,
        IIoService io,
        UnitSettings units,
        ICamera camera,
        ILightController light,
        LightingSettings lightingSettings,
        RecipeManager recipes,
        ILogger<InspectionStation>? log = null)
        : base(io, [
            InputIo.NgCarrierPickupUp,
            InputIo.NgCarrierPickupDown,
            InputIo.NgCarrierGripperOpen,
            InputIo.NgCarrierGripperClosed,
        ], [OutputIo.MainConveyorRun])
    {
        _log = log;
        Station = station;
        Motion = motionStatus;
        _motion = motion;
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
        ngConveyor.AttachTransfer(this);
        ngConveyor.Changed += NotifyChanged;
        recipes.Changed += NotifyChanged;
        recipes.InspectionSettingsChanged += NotifyChanged;
        StepChanged += NotifyChanged;
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

    public StationCylinderState Lift
    {
        get
        {
            switch ((Io.GetInput(InputIo.NgCarrierPickupUp), Io.GetInput(InputIo.NgCarrierPickupDown)))
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
                Io.GetInput(InputIo.NgCarrierGripperOpen),
                Io.GetInput(InputIo.NgCarrierGripperClosed)))
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

    public bool IsRaised => Io.GetInput(InputIo.NgCarrierPickupUp) && !Io.GetInput(InputIo.NgCarrierPickupDown);

    public bool IsClear => IsRaised && !IsTransferPending;

    public bool IsAtInspectionPosition
    {
        get
        {
            return Station.CarrierPresent
                && Station.BackupPlate == StationCylinderState.Down
                && Station.Stopper == StationCylinderState.Up
                && !Io.GetOutput(OutputIo.MainConveyorRun);
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
            if (!Station.CarrierPresent)
                return false;
            if (Station.HasNg)
                return true;
            if (!Station.Completed)
                return false;
            // A completed carrier still goes to NG if required measurements are missing.
            if (Station.Assemblies.Any(assembly => assembly.TurnsResult == AssemblyResult.Pending))
                return true;
            return _units.Inspection
                && !Station.Assemblies.Any(assembly => assembly.InspectionResult != AssemblyResult.Pending);
        }
    }

    internal bool IsWaitingForConveyor
    {
        get
        {
            return Station.CarrierPresent && !Station.Completed
                && _units.MainConveyor && !InspectionRequested;
        }
    }

    private bool IsReadyToInspect
    {
        get
        {
            return Station.CarrierPresent && !Station.Completed
                && (!_units.MainConveyor || InspectionRequested)
                && IsAtInspectionPosition && IsClear;
        }
    }

    public bool IsRearDischargeReady
    {
        get
        {
            if (!IsClear)
                return false;
            if (!_units.Inspection)
                return true;
            // Waiting is entered only after the return move completes in this run.
            return Step is InspectionStationState.Waiting
                && MotionServiceBase.IsReadyAndStopped(_motion);
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

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        try
        {
            BeginRun();
            while (!cancellationToken.IsCancellationRequested)
            {
                var step = !_units.Inspection && !CarrierSeatingRequested
                    ? InspectionStationState.Disabled : NextStep;
                if (!await ExecuteStepAsync(step, cancellationToken))
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

    internal InspectionStationState NextStep
    {
        get
        {
            if (_waitingForShuttleDown && IsClear && Gripper == NgTransferGripperState.Open)
                return _ngConveyor.ShuttleLift == StationCylinderState.Down
                    ? InspectionStationState.ReturningToWaitingPosition
                    : InspectionStationState.WaitingForShuttleDown;
            if (CarrierSeatingRequested)
                return InspectionStationState.SeatingCarrier;
            if (!_units.Inspection)
                return InspectionStationState.Disabled;
            if (IsTransferPending)
            {
                return !IsRaised || Gripper != NgTransferGripperState.Closed || _ngConveyor.IsReceiveAllowed
                    ? InspectionStationState.PlacingCarrier : InspectionStationState.WaitingForDestination;
            }
            if (!IsRaised || Gripper != NgTransferGripperState.Open)
                return InspectionStationState.PreparingTransfer;
            if (Station.CarrierSeated && Station.Completed && RouteToNg)
            {
                return IsSupportReady(NgTransferDestination.Station) && _ngConveyor.IsReceiveAllowed
                    ? InspectionStationState.PickingCarrier : InspectionStationState.WaitingForDestination;
            }

            if (!IsReadyToInspect)
                return IsWaitingForConveyor
                    ? InspectionStationState.WaitingForConveyor : InspectionStationState.Waiting;
            if (_runJob is null)
                return InspectionStationState.PreparingInspection;
            var target = InspectionTarget;
            if (target.Pcb is null)
                return InspectionStationState.CompletingInspection;
            var taught = target.Bolt is null ? HasBarcodeRegion(target.Pcb.Value) : HasRegion(target.Bolt);
            return taught ? InspectionStationState.InspectingPoint : InspectionStationState.TeachingRequired;
        }
    }

    private async Task<bool> ExecuteStepAsync(
        InspectionStationState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = _inspectionOperation;
        try
        {
            CheckInspectionPosition();
            operation?.Token.ThrowIfCancellationRequested();
            switch (state)
            {
                case InspectionStationState.PreparingTransfer
                    or InspectionStationState.PickingCarrier
                    or InspectionStationState.PlacingCarrier
                    or InspectionStationState.WaitingForDestination:
                    return await ExecuteTransferAsync(state, cancellationToken);
                case InspectionStationState.PreparingInspection:
                    EnterStep(state, workId: Station.CurrentJob.Id);
                    ClearInspectionOperation();
                    _cycleStartedAt = Stopwatch.GetTimestamp();
                    var points = new List<(HeatSinkSlot Pcb, BoltPoint? Bolt)>();
                    foreach (var pcb in Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent))
                    {
                        var pcbBolts = _recipes.Current.Pcb.BoltPoints.Where(bolt => bolt.HeatSink == pcb);
                        if (!pcbBolts.Any())
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
                    EnterStep(state, workId: Station.CurrentJob.Id);
                    await SeatStationAsync(cancellationToken);
                    ClearCarrierSeatingRequest();
                    return true;
                case InspectionStationState.ReturningToWaitingPosition:
                    EnterStep(state, workId: Station.CurrentJob.Id);
                    await MoveToWaitingPositionAsync(cancellationToken);
                    return true;
                case InspectionStationState.Disabled:
                    EnterStep(state, workId: Station.CurrentJob.Id);
                    if (Station.CarrierSeated || IsAtInspectionPosition)
                        Station.Complete();
                    return false;
                case InspectionStationState.Waiting or InspectionStationState.WaitingForConveyor:
                case InspectionStationState.TeachingRequired:
                    // A change of waiting reason does not require another return move.
                    if (IsClear && Step is not InspectionStationState.Waiting
                            and not InspectionStationState.WaitingForConveyor
                            and not InspectionStationState.TeachingRequired
                            and not InspectionStationState.ReturningToWaitingPosition
                            and not InspectionStationState.CompletingInspection)
                    {
                        EnterStep(InspectionStationState.ReturningToWaitingPosition, workId: Station.CurrentJob.Id);
                        await MoveToWaitingPositionAsync(cancellationToken);
                    }
                    EnterStep(state, workId: Station.CurrentJob.Id);
                    return false;
                case InspectionStationState.WaitingForShuttleDown:
                    EnterStep(state, workId: Station.CurrentJob.Id, waitingFor: $"shuttle Down; current={_ngConveyor.ShuttleLift}");
                    return false;
                case InspectionStationState.CompletingInspection:
                {
                    EnterStep(state, workId: Station.CurrentJob.Id);
                    var token = operation?.Token ?? throw new InvalidOperationException("No inspection work is selected.");
                    var job = _runJob!;
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    await MoveToWaitingPositionAsync(token);
                    token.ThrowIfCancellationRequested();
                    var okCount = 0;
                    var ngCount = 0;
                    foreach (var point in _runPoints!)
                    {
                        if (point.Bolt is not null)
                            continue;
                        var assembly = Station.GetAssembly(job, point.Pcb);
                        assembly.CompleteInspection();
                        switch (assembly.Result)
                        {
                            case AssemblyResult.Ok:
                                okCount++;
                                break;
                            case AssemblyResult.Ng:
                                ngCount++;
                                break;
                        }
                    }
                    var cycleTime = Stopwatch.GetElapsedTime(_cycleStartedAt);
                    await _recipes.RecordProductionAsync(okCount, ngCount);
                    Station.Complete(job, cycleTime);
                    ClearInspectionOperation();
                    return true;
                }
                case InspectionStationState.InspectingPoint:
                {
                    var target = InspectionTarget;
                    var pcb = target.Pcb ?? throw new InvalidOperationException("No inspection target is selected.");
                    var bolt = target.Bolt;
                    var pointName = bolt is null ? "Data Matrix" : $"Bolt {_recipes.Current.Pcb.GetBoltOrdinal(bolt.Id)}";
                    EnterStep(state, $"{pcb.GetDescription()} / {pointName}", Station.CurrentJob.Id);
                    var token = operation?.Token ?? throw new InvalidOperationException("No inspection work is selected.");
                    var job = _runJob!;
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    var assembly = Station.GetAssembly(job, pcb);
                    var capture = bolt is null
                        ? await ReadBarcodeAsync(pcb, token)
                        : await InspectAsync(bolt, token);
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    if (bolt is null)
                        assembly.PcbBarcode = capture.Barcode;
                    else
                        assembly.RecordBoltPresence(bolt.Id, capture.Success);
                    assembly.RecordInspectionCapture(capture);
                    _pointIndex++;
                    NotifyChanged();
                    return true;
                }
                default:
                    EnterStep(state, workId: Station.CurrentJob.Id);
                    throw new ArgumentOutOfRangeException(nameof(state));
            }
        }
        catch (OperationCanceledException) when (operation?.IsCancellationRequested == true
            && !cancellationToken.IsCancellationRequested)
        {
            throw new MotionInterlockException(
                "Inspection carrier feedback changed during inspection. Check the PCB presence and supports before restarting.");
        }
    }

    private void CheckInspectionPosition()
    {
        if (_inspectionOperation is not { } operation)
            return;
        lock (operation)
        {
            if (ReferenceEquals(operation, _inspectionOperation)
                && (!IsReadyToInspect || !ReferenceEquals(_runJob, Station.CurrentJob)
                    || _runPoints is { } points && Enum.GetValues<HeatSinkSlot>().Any(pcb =>
                        Station.IsHeatSinkPresent(pcb) != points.Any(point => point.Pcb == pcb))))
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
        _runPoints = null;
        _pointIndex = 0;
        NotifyChanged();
    }

    public async Task MoveToWaitingPositionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var position = _settings.WaitingPosition
            ?? throw new InvalidOperationException("Record Inspection Waiting X/Y before moving to the inspection waiting position.");
        await MoveToAsync(position, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_waitingForShuttleDown)
        {
            _waitingForShuttleDown = false;
            NotifyChanged();
        }
    }

    // Each operation completes its moves before advancing; coordinates do not select a step.
    internal async Task<bool> ExecuteTransferAsync(
        InspectionStationState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (state)
        {
            case InspectionStationState.PreparingTransfer:
                EnterStep(state, nameof(NgTransferDestination.Shuttle));
                if (!IsRaised)
                    await SetLiftUpAsync(true, cancellationToken);
                if (!IsTransferPending && Gripper != NgTransferGripperState.Open)
                    await SetGripperOpenAsync(true, cancellationToken);
                break;
            case InspectionStationState.PickingCarrier:
            {
                EnterStep(state, nameof(NgTransferDestination.Shuttle));
                if (IsTransferPending)
                    throw new InvalidOperationException("Finish the pending NG transfer before picking another carrier.");
                await SetLiftUpAsync(true, cancellationToken);
                await SetGripperOpenAsync(true, cancellationToken);
                await MoveToCarrierAsync(NgTransferDestination.Station, cancellationToken);
                if (!IsSupportReady(NgTransferDestination.Station) || !Station.CarrierPresent)
                    return false;
                await SetLiftUpAsync(false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // Descent can outlive the source's support or carrier feedback.
                if (!IsSupportReady(NgTransferDestination.Station) || !Station.CarrierPresent)
                    return false;
                IsTransferPending = true;
                await SetGripperOpenAsync(false, cancellationToken);
                await SetLiftUpAsync(true, cancellationToken);
                // The raised pickup now supports the carrier; lower S3 before travelling to the shuttle.
                await Io.SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, false, cancellationToken);
                break;
            }
            case InspectionStationState.PlacingCarrier:
            {
                EnterStep(state, nameof(NgTransferDestination.Shuttle));
                using var carrying = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                void CheckGrip()
                {
                    if (!IsTransferPending || Gripper != NgTransferGripperState.Closed)
                        OperationCancellation.CancelIfNotDisposed(carrying);
                }
                Changed += CheckGrip;
                try
                {
                    CheckGrip();
                    carrying.Token.ThrowIfCancellationRequested();
                    // A stopped, lowered release is not resumed by guessing its XY location.
                    if (!IsRaised)
                        throw new InvalidOperationException("Raise the gripped NG carrier before transferring it.");
                    await MoveToAsync(_settings.ShuttlePlacePosition, cancellationToken: carrying.Token);
                    carrying.Token.ThrowIfCancellationRequested();
                    // Keep the closed gripper while waiting for the receiving support.
                    while (!IsSupportReady(NgTransferDestination.Shuttle))
                    {
                        EnterStep(InspectionStationState.WaitingForDestination, nameof(NgTransferDestination.Shuttle));
                        await WaitForChangeAsync(carrying.Token);
                    }
                    EnterStep(state, nameof(NgTransferDestination.Shuttle));
                    await SetLiftUpAsync(false, carrying.Token);
                    carrying.Token.ThrowIfCancellationRequested();
                    if (!IsSupportReady(NgTransferDestination.Shuttle) || Lift != StationCylinderState.Down
                        || !Io.GetInput(InputIo.NgShuttleCarrierDetected))
                        throw new InvalidOperationException("Confirm the destination supports the pending NG carrier before releasing it.");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("NG transfer lost confirmed grip while carrying or lowering the carrier.");
                }
                finally
                {
                    Changed -= CheckGrip;
                }

                _waitingForShuttleDown = true;
                await SetGripperOpenAsync(true, cancellationToken);
                await Io.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, true, cancellationToken, requireCurrent: true);
                await SetLiftUpAsync(true, cancellationToken);
                break;
            }
            case InspectionStationState.WaitingForDestination:
                EnterStep(state, nameof(NgTransferDestination.Shuttle), waitingFor:
                    $"source support={IsSupportReady(NgTransferDestination.Station)}, "
                        + $"destination support={IsSupportReady(NgTransferDestination.Shuttle)}, destination occupied={Io.GetInput(InputIo.NgShuttleCarrierDetected)}, "
                        + $"shuttle={_ngConveyor.ShuttleLift}, receive={_ngConveyor.IsReceiveAllowed}, "
                        + $"pickup={Lift}, gripper={Gripper}, pending={IsTransferPending}");
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported inspection transfer step.");
        }
        return true;
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

    private bool IsSupportReady(NgTransferDestination location)
    {
        return location == NgTransferDestination.Station
            ? Station.BackupPlate == StationCylinderState.Up
                && Station.Stopper == StationCylinderState.Down
            : Io.GetInput(InputIo.NgShuttleUp) && !Io.GetInput(InputIo.NgShuttleDown);
    }

    public Task SetLiftUpAsync(bool up, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (up && IsTransferPending && Gripper != NgTransferGripperState.Closed)
            throw new MotionInterlockException("Confirm the NG gripper is closed before raising the pending transfer.");
        return Io.SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, !up, cancellationToken);
    }

    public async Task SetGripperOpenAsync(bool open, CancellationToken cancellationToken = default)
    {
        await Io.SetOutputAndWaitAsync(OutputIo.NgCarrierGripperClose, !open, cancellationToken);
        if (open && Gripper == NgTransferGripperState.Open)
            IsTransferPending = false;
    }

    public async Task<bool> HomeAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return await _motion.HomeAsync(axis, _motionSettings.Home(axis).SearchSpeed, cancellationToken);
    }

    public async Task<bool> HomeHorizontalAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return await _motion.HomeHorizontalAsync(_motionSettings.HorizontalHome.SearchSpeed, cancellationToken);
    }

    public async Task MoveToCarrierAsync(
        NgTransferDestination source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var position = (source == NgTransferDestination.Station
            ? _settings.CarrierPickupPosition : _settings.ShuttlePlacePosition)
            ?? throw new InvalidOperationException("Record Carrier Pickup (S3) X/Y before moving to a carrier.");
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

    public event Action<ImageFrame, HeatSinkSlot, Guid?>? InspectionCaptured;

    // Once per completed carrier, counted by individual PCB verdicts.

    public event Action<ImageFrame>? FrameReady
    {
        add => _camera.FrameReady += value;
        remove => _camera.FrameReady -= value;
    }

    public bool IsLiveView => _camera.IsLiveView;

    public Exception? LiveViewError
    {
        get;
        private set
        {
            field = value;
            LiveViewChanged?.Invoke();
        }
    }

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

        LiveViewError = failure;
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    public bool HasBarcodeRegion(HeatSinkSlot pcb)
    {
        var size = _camera.FrameSize;
        return _recipes.Current.FindInspectionImage(pcb, boltId: null) is { Center: not null, Region: { } region }
            && region.IsInside(size.Width, size.Height);
    }

    public CarrierImageTile GetBarcodeFov(HeatSinkSlot pcb)
    {
        var fov = _recipes.Current.CarrierImages.SingleOrDefault(item => item.IsForTarget(pcb, boltId: null));
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
        if (!HasBarcodeRegion(pcb))
            throw new InvalidOperationException($"Teach a FOV and ROI for {pcb.GetDescription()} Data Matrix.");
        var fov = GetBarcodeFov(pcb);
        await MoveToAsync(_recipes.Current.GetInspectionPosition(fov), cancellationToken: cancellationToken);
        var image = await CaptureCurrentAsync(cancellationToken,
            lightLevel: _recipes.Current.BoltInspection.GetDataMatrix(pcb).LightLevel
                ?? _recipes.Current.BoltInspection.LightLevel);
        var capturedAt = DateTimeOffset.Now;
        InspectionCaptured?.Invoke(image, pcb, null);
        return await Task.Run(() =>
        {
            var region = fov.Region!;
            var decoder = _recipes.Current.BoltInspection.GetDataMatrix(pcb);
            _log?.LogInformation(
                "Data Matrix inspection: recipe={Recipe}, PCB={Pcb}, thresholds={Minimum}..{Maximum}, step={Step}, dilation radius={Radius}, ROI={Region}.",
                _recipes.Current.Name, pcb, decoder.ThresholdMinimum, decoder.ThresholdMaximum,
                decoder.ThresholdStep, decoder.DilationRadius, region);
            var result = DataMatrixReader.Read(image, region, decoder, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _log?.LogInformation(
                "Data Matrix result: PCB={Pcb}, read={Read}, threshold={Threshold}, dilated={Dilated}.",
                pcb, !string.IsNullOrEmpty(result.Text), result.Threshold, result.Dilated);
            return new InspectionCapture(null, capturedAt, image, region, !string.IsNullOrEmpty(result.Text),
                Barcode: result.Text, Threshold: result.Threshold, Dilated: result.Dilated);
        }, cancellationToken);
    }

    public bool HasRegion(BoltPoint point)
    {
        var size = _camera.FrameSize;
        return point.InspectionPosition is not null
            && _recipes.Current.FindInspectionImage(point.HeatSink, point.Id)?.Region is { } region
            && region.IsInside(size.Width, size.Height);
    }

    public Task MoveToBoltAsync(BoltPoint point, CancellationToken cancellationToken = default)
    {
        var position = point.InspectionPosition ?? throw new InvalidOperationException(
            $"Record an inspection position for {point.HeatSink.GetDescription()} bolt {point.Id}.");
        return MoveToAsync(position, cancellationToken: cancellationToken);
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
        if (!HasRegion(point))
            throw new InvalidOperationException($"Teach a FOV and ROI for {point.HeatSink.GetDescription()} bolt {point.Id}.");
        var fov = _recipes.Current.FindInspectionImage(point.HeatSink, point.Id)
            ?? throw new InvalidOperationException(
                $"Record a position for {point.HeatSink.GetDescription()} bolt {point.Id}.");
        await MoveToAsync(_recipes.Current.GetInspectionPosition(fov), cancellationToken: cancellationToken).ConfigureAwait(false);
        var image = await CaptureCurrentAsync(cancellationToken,
            lightLevel: point.LightLevel ?? _recipes.Current.BoltInspection.LightLevel).ConfigureAwait(false);
        var capturedAt = DateTimeOffset.Now;
        InspectionCaptured?.Invoke(image, point.HeatSink, point.Id);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var region = fov.Region!;
            var ratio = BinaryRegionAnalyzer.Check(image, region,
                point.BrightnessThreshold ?? _recipes.Current.BoltInspection.BrightnessThreshold).BrightRatio;
            var minimum = point.MinimumBrightRatio ?? _recipes.Current.BoltInspection.MinimumBrightRatio;
            cancellationToken.ThrowIfCancellationRequested();
            return new InspectionCapture(point.Id, capturedAt, image, region, ratio >= minimum,
                BrightRatio: ratio, MinimumBrightRatio: minimum);
        }, cancellationToken);
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
            LiveViewError = cancelled ? null : exception;
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

        LiveViewError = failure;
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
        LiveViewError = failure;
        _log?.LogError(failure, "Inspection live view failed.");
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
