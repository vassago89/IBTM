using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BackupPlate;
using IBTM.Core;
using IBTM.Device;
using IBTM.Mlcp;
using IBTM.NgConveyor;
using IBTM.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IBTM.Inspection;

public sealed record CarrierImage(AxisPosition Center, ImageFrame Frame);

public sealed class InspectionStation : AutoUnit, INgCarrierTransferFeedback
{
    private readonly ILogger<InspectionStation>? _log;
    private readonly IXyMotion _motion;
    private readonly InspectionGantrySettings _gantrySettings;
    private readonly NgCarrierTransferSettings _settings;
    private readonly NgCarrierConveyor _ngConveyor;
    private readonly UnitSettings _units;
    // An unfinished shuttle handoff must still wait for clearance after STOP.
    private bool _waitingForShuttleDown;
    // Selected work belongs only to this run; STOP starts again at the first point.
    private (HeatSinkSlot Pcb, BoltPoint? Bolt, InspectionPointKind Kind)[]? _runPoints;
    private int _pointIndex;
    private long _cycleStartedAt;
    private BackupPlateStation.Job? _runJob;
    private CancellationTokenSource? _inspectionOperation;
    // Scheduling ownership for this job only; never a physical position or restart checkpoint.
    private volatile BackupPlateStation.Job? _inspectionRequestedJob;
    private volatile BackupPlateStation.Job? _carrierSeatingRequestedJob;
    private readonly ICamera _camera;
    private readonly ILightController _light;
    private readonly LightingSettings _lightingSettings;
    private readonly RecipeManager _recipes;
    private readonly SemaphoreSlim _visionGate;
    private int? _lightChannel;
    private readonly MlcpController _measurement;
    private readonly MlcpSettings _measurementSettings;

    public InspectionStation(
        [FromKeyedServices(MotionGroup.InspectionGantry)] BackupPlateStation station,
        [FromKeyedServices(MotionGroup.InspectionGantry)] IXyMotion motion,
        [FromKeyedServices(MotionGroup.InspectionGantry)] MotionStatus motionStatus,
        NgCarrierConveyor ngConveyor,
        InspectionGantrySettings motionSettings,
        NgCarrierTransferSettings settings,
        IIoService io,
        UnitSettings units,
        ICamera camera,
        ILightController light,
        LightingSettings lightingSettings,
        RecipeManager recipes,
        MlcpController measurement,
        MlcpSettings measurementSettings,
        ILogger<InspectionStation>? log = null)
        : base(io, [
            InputIo.NgCarrierPickupUp,
            InputIo.NgCarrierPickupDown,
            InputIo.NgCarrierGripperOpen,
            InputIo.NgCarrierGripperClosed,
            InputIo.InspectionProbeUp,
            InputIo.InspectionProbeDown,
        ], [OutputIo.MainConveyorRun])
    {
        _log = log;
        Station = station;
        Motion = motionStatus;
        _motion = motion;
        _gantrySettings = motionSettings;
        _settings = settings;
        _ngConveyor = ngConveyor;
        _units = units;
        _camera = camera;
        _light = light;
        _lightingSettings = lightingSettings;
        _recipes = recipes;
        _measurement = measurement;
        _measurementSettings = measurementSettings;
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

    private (HeatSinkSlot? Pcb, BoltPoint? Bolt, InspectionPointKind Kind) InspectionTarget
    {
        get
        {
            var index = _pointIndex;
            return _runPoints is { } points && index < points.Length ? points[index] : (null, null, InspectionPointKind.Photo);
        }
    }

    public BackupPlateStation Station { get; }

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

    public bool IsHorizontalMoveAllowed => IsRaised
        && Io.GetInput(InputIo.InspectionProbeUp) && !Io.GetInput(InputIo.InspectionProbeDown)
        && (!_waitingForShuttleDown || _ngConveyor.ShuttleLift == StationCylinderState.Down);

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
            if (Station.Assemblies.Any(assembly => assembly.Result == AssemblyResult.Ng))
                return true;
            if (!Station.Completed)
                return false;
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

    public void RequestInspection(BackupPlateStation.Job job)
    {
        Station.RequireCurrentJob(job);
        if (!IsAtInspectionPosition || !IsClear)
            throw new InvalidOperationException(UiText.Get("Inspection requires a present carrier, plate DOWN, stopper UP, stopped belt and clear pickup."));
        _inspectionRequestedJob = job;
        NotifyChanged();
    }

    public void ClearInspectionRequest()
    {
        _inspectionRequestedJob = null;
        _carrierSeatingRequestedJob = null;
        NotifyChanged();
    }

    public void RequestCarrierSeating(BackupPlateStation.Job job)
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
            if (target.Kind == InspectionPointKind.Fiducials)
                return InspectionStationState.FindingFiducials;
            if (target.Kind is InspectionPointKind.ReferencePlane or InspectionPointKind.Height)
                return InspectionStationState.MeasuringHeight;
            var taught = target.Bolt is null ? HasBarcodeRegion(target.Pcb.Value) : target.Bolt.InspectionPosition is not null;
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
                    var points = new List<(HeatSinkSlot Pcb, BoltPoint? Bolt, InspectionPointKind Kind)>();
                    foreach (var pcb in Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent))
                    {
                        var pcbBolts = _recipes.Current.Pcb.BoltPoints.Where(bolt => bolt.HeatSink == pcb).ToArray();
                        if (pcbBolts.Length == 0)
                            throw new InvalidOperationException(
                                UiText.Format($"{UiText.Get(pcb)} has no taught bolts. Complete bolt teaching before inspection."));
                        _recipes.Current.BoltInspection.GetHeightReference(pcb).Validate();
                        foreach (var bolt in pcbBolts)
                        {
                            if (bolt.MinimumHeight is not { } minimum || bolt.MaximumHeight is not { } maximum || minimum > maximum)
                                throw new InvalidOperationException(UiText.Format($"Set the height limits for {BoltPoint.GetDisplayName(bolt.Name, _recipes.Current.Pcb.GetBoltOrdinal(bolt.Id))}"));
                        }
                        var fiducials = _recipes.Current.BoltInspection.GetFiducials(pcb);
                        if (_gantrySettings.CameraResolution is null)
                            throw new InvalidOperationException(UiText.Get("Set camera resolution"));
                        if (!fiducials.First.IsRegistered || !fiducials.Second.IsRegistered)
                            throw new InvalidOperationException(UiText.Get("Register both fiducials"));
                        points.Add((pcb, null, InspectionPointKind.Fiducials));
                        points.Add((pcb, null, InspectionPointKind.Photo));
                        foreach (var bolt in pcbBolts)
                            points.Add((pcb, bolt, InspectionPointKind.Photo));
                        points.Add((pcb, null, InspectionPointKind.ReferencePlane));
                        foreach (var bolt in pcbBolts)
                            points.Add((pcb, bolt, InspectionPointKind.Height));
                    }
                    await PrepareMeasurementAsync(cancellationToken);
                    foreach (var point in points.Where(point => point.Kind == InspectionPointKind.ReferencePlane))
                        Station.GetAssembly(point.Pcb).ClearInspectionResults();
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
                    var token = operation?.Token ?? throw new InvalidOperationException(UiText.Get("No inspection work is selected."));
                    var job = _runJob!;
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    await MoveToWaitingPositionAsync(token);
                    token.ThrowIfCancellationRequested();
                    var okCount = 0;
                    var ngCount = 0;
                    foreach (var point in _runPoints!)
                    {
                        if (point.Bolt is not null || point.Kind != InspectionPointKind.Photo)
                            continue;
                        var assembly = Station.GetAssembly(job, point.Pcb);
                        if (assembly.HeightReference is null || _runPoints.Any(target =>
                            target.Pcb == point.Pcb && target.Kind == InspectionPointKind.Height && target.Bolt is { } bolt
                            && !assembly.HeightResults.ContainsKey(bolt.Id)))
                            throw new InvalidOperationException(UiText.Get("Height measurement is incomplete"));
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
                case InspectionStationState.FindingFiducials:
                {
                    var job = _runJob ?? throw new InvalidOperationException(UiText.Get("Inspection has no active carrier job."));
                    var pcb = InspectionTarget.Pcb!.Value;
                    var token = _inspectionOperation!.Token;
                    var fiducials = _recipes.Current.BoltInspection.GetFiducials(pcb);
                    var markName = UiText.Get("Fiducial 1");
                    try
                    {
                        EnterStep(state, $"{pcb.GetDescription()} / {markName}", job.Id);
                        var first = await FindFiducialAsync(fiducials.First, token);
                        markName = UiText.Get("Fiducial 2");
                        EnterStep(state, $"{pcb.GetDescription()} / {markName}", job.Id);
                        var second = await FindFiducialAsync(fiducials.Second, token);
                        token.ThrowIfCancellationRequested();
                        Station.RequireCurrentJob(job);
                        var alignment = FiducialDetector.GetAlignment(first, second);
                        Station.GetAssembly(job, pcb).RecordAlignment(alignment);
                        _log?.LogInformation("Fiducials {Pcb}: angle={Angle}, first={First}, second={Second}.",
                            pcb, alignment.AngleDegrees, first, second);
                    }
                    catch (FiducialDetectionException exception)
                    {
                        token.ThrowIfCancellationRequested();
                        Station.RequireCurrentJob(job);
                        var error = $"{markName}: {UiText.Get(exception)}";
                        Station.GetAssembly(job, pcb).RecordFiducialFailure(error, _gantrySettings.ExcludeFiducialFromNg);
                        _log?.LogWarning("Fiducials {Pcb} failed: {Error}. Continuing at taught coordinates; excluded from NG={Excluded}.",
                            pcb, error, _gantrySettings.ExcludeFiducialFromNg);
                    }
                    _pointIndex++;
                    NotifyChanged();
                    return true;
                }
                case InspectionStationState.MeasuringHeight:
                {
                    var target = InspectionTarget;
                    var pcb = target.Pcb ?? throw new InvalidOperationException(UiText.Get("No inspection target is selected."));
                    var token = operation?.Token ?? throw new InvalidOperationException(UiText.Get("No inspection work is selected."));
                    var job = _runJob!;
                    Station.RequireCurrentJob(job);
                    var assembly = Station.GetAssembly(job, pcb);
                    if (target.Bolt is { } bolt)
                    {
                        EnterStep(state, $"{pcb.GetDescription()} / Bolt {_recipes.Current.Pcb.GetBoltOrdinal(bolt.Id)}", job.Id);
                        var minimum = bolt.MinimumHeight ?? throw new InvalidOperationException(UiText.Get("Height limits are not set"));
                        var maximum = bolt.MaximumHeight ?? throw new InvalidOperationException(UiText.Get("Height limits are not set"));
                        if (minimum > maximum)
                            throw new InvalidOperationException(UiText.Get("Minimum height exceeds maximum height"));
                        var position = assembly.Alignment?.Transform(bolt.InspectionPosition!) ?? bolt.InspectionPosition!;
                        var sample = await MeasureHeightAtAsync(position, token);
                        token.ThrowIfCancellationRequested();
                        Station.RequireCurrentJob(job);
                        var plane = assembly.HeightReference ?? throw new InvalidOperationException(UiText.Get("Height reference is missing"));
                        var result = new BoltHeightResult(DateTimeOffset.Now, sample,
                            plane.HeightAt(sample.X, sample.Y), minimum, maximum);
                        assembly.RecordHeight(bolt.Id, result);
                        _log?.LogInformation("Height {Pcb}/{Bolt}: raw={Raw}, reference={Reference}, height={Height}, limits={Minimum}..{Maximum}, OK={Success}.",
                            pcb, bolt.Id, sample.Value, result.ReferenceHeight, result.Height, minimum, maximum, result.Success);
                    }
                    else
                    {
                        var reference = _recipes.Current.BoltInspection.GetHeightReference(pcb);
                        EnterStep(state, $"{pcb.GetDescription()} / Reference 1", job.Id);
                        var p1 = await MeasureHeightAtAsync(assembly.Alignment?.Transform(reference.Point1!) ?? reference.Point1!, token);
                        EnterStep(state, $"{pcb.GetDescription()} / Reference 2", job.Id);
                        var p2 = await MeasureHeightAtAsync(assembly.Alignment?.Transform(reference.Point2!) ?? reference.Point2!, token);
                        EnterStep(state, $"{pcb.GetDescription()} / Reference 3", job.Id);
                        var p3 = await MeasureHeightAtAsync(assembly.Alignment?.Transform(reference.Point3!) ?? reference.Point3!, token);
                        token.ThrowIfCancellationRequested();
                        Station.RequireCurrentJob(job);
                        var plane = new HeightReferencePlane(DateTimeOffset.Now, p1, p2, p3);
                        _ = plane.HeightAt(p1.X, p1.Y);
                        assembly.RecordHeightReference(plane);
                    }
                    _pointIndex++;
                    NotifyChanged();
                    return true;
                }
                case InspectionStationState.InspectingPoint:
                {
                    var target = InspectionTarget;
                    var pcb = target.Pcb ?? throw new InvalidOperationException(UiText.Get("No inspection target is selected."));
                    var bolt = target.Bolt;
                    var pointName = bolt is null ? "Data Matrix" : $"Bolt {_recipes.Current.Pcb.GetBoltOrdinal(bolt.Id)}";
                    EnterStep(state, $"{pcb.GetDescription()} / {pointName}", Station.CurrentJob.Id);
                    var token = operation?.Token ?? throw new InvalidOperationException(UiText.Get("No inspection work is selected."));
                    var job = _runJob!;
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    var assembly = Station.GetAssembly(job, pcb);
                    var capture = bolt is null
                        ? await ReadBarcodeAsync(pcb, token, assembly.Alignment)
                        : await CaptureBoltAsync(bolt, token, assembly.Alignment);
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    if (bolt is null)
                    {
                        assembly.IsDataMatrixNgExcluded = _gantrySettings.ExcludeDataMatrixFromNg;
                        assembly.PcbBarcode = capture.Barcode;
                    }
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
                UiText.Get("Inspection carrier feedback changed during inspection. Check the PCB presence and supports before restarting."));
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
            ?? throw new InvalidOperationException(UiText.Get("Record Inspection Waiting X/Y before moving to the inspection waiting position."));
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
                // A closed gripper without pickup history may still hold a carrier after restart.
                if (!IsTransferPending && Gripper != NgTransferGripperState.Open)
                    throw new InvalidOperationException(UiText.Get("Support the carrier, then open the NG gripper."));
                if (!IsRaised)
                    await SetLiftUpAsync(true, cancellationToken);
                break;
            case InspectionStationState.PickingCarrier:
            {
                EnterStep(state, nameof(NgTransferDestination.Shuttle));
                if (IsTransferPending)
                    throw new InvalidOperationException(UiText.Get("Finish the pending NG transfer before picking another carrier."));
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
                await Station.SetBackupPlateUpAsync(false, cancellationToken);
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
                        throw new InvalidOperationException(UiText.Get("Raise the gripped NG carrier before transferring it."));
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
                        throw new InvalidOperationException(UiText.Get("Confirm the destination supports the pending NG carrier before releasing it."));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException(UiText.Get("NG transfer lost confirmed grip while carrying or lowering the carrier."));
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
                throw new ArgumentOutOfRangeException(nameof(state), state, UiText.Get("Unsupported inspection transfer step."));
        }
        return true;
    }

    public async Task SeatStationAsync(CancellationToken cancellationToken)
    {
        var position = _settings.CarrierPickupPosition
            ?? throw new InvalidOperationException(UiText.Get("Record Carrier Pickup (S3) X/Y before raising the inspection backup plate."));
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
            throw new MotionInterlockException(UiText.Get("Confirm the NG gripper is closed before raising the pending transfer."));
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
        return await _motion.HomeAsync(axis, _gantrySettings.Motion.Home(axis).SearchSpeed, cancellationToken);
    }

    public async Task<bool> HomeHorizontalAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return await _motion.HomeHorizontalAsync(_gantrySettings.Motion.HorizontalHome.SearchSpeed, cancellationToken);
    }

    public async Task MoveToCarrierAsync(
        NgTransferDestination source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var position = (source == NgTransferDestination.Station
            ? _settings.CarrierPickupPosition : _settings.ShuttlePlacePosition)
            ?? throw new InvalidOperationException(UiText.Get("Record Carrier Pickup (S3) X/Y before moving to a carrier."));
        await MoveToAsync(position, cancellationToken: cancellationToken);
    }

    public Task MoveToAsync(
        AxisPosition position,
        double? velocity = null,
        CancellationToken cancellationToken = default)
    {
        EnsureCanMove(cancellationToken);
        return _motion.MoveToXYAsync(
            position.X, position.Y, velocity ?? _gantrySettings.Motion.HorizontalSpeed, cancellationToken);
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
        if (!Io.GetInput(InputIo.InspectionProbeUp) || Io.GetInput(InputIo.InspectionProbeDown))
            throw new MotionInterlockException(UiText.Get("Raise the inspection probe before XY movement"));
        if (!IsRaised)
        {
            throw new MotionInterlockException(UiText.Get("NG carrier pickup must be raised before inspection XY movement."));
        }
        if (_waitingForShuttleDown && _ngConveyor.ShuttleLift != StationCylinderState.Down)
        {
            throw new MotionInterlockException(UiText.Get("Wait for the NG shuttle to finish lowering after carrier release before inspection XY movement."));
        }
    }

    public event Action? LiveViewChanged;

    public async Task<FiducialObservation> FindFiducialAsync(FiducialMark mark, CancellationToken cancellationToken)
    {
        var target = _gantrySettings.GetFiducialPosition(mark)
            ?? throw new InvalidOperationException(UiText.Get("Register both fiducials"));
        await MoveToAsync(target, cancellationToken: cancellationToken);
        var image = await CaptureCurrentAsync(cancellationToken, lightLevel: mark.LightLevel ?? _recipes.Current.BoltInspection.LightLevel);
        var detection = await Task.Run(() => FiducialDetector.Detect(image, mark, _gantrySettings.CameraResolution), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var current = _motion.Position;
        var measured = _gantrySettings.ToMachinePosition(new() { X = current.X, Y = current.Y },
            detection.Center, image.Width, image.Height);
        return new(new(target.X, target.Y), measured, detection.ShapeError, detection.MatchScore);
    }

    public async Task PrepareMeasurementAsync(CancellationToken cancellationToken)
    {
        // Unassigned I/O must fail before XY movement or a measurement request.
        _ = Io.GetInput(InputIo.InspectionProbeUp);
        _ = Io.GetInput(InputIo.InspectionProbeDown);
        _ = Io.GetOutput(OutputIo.InspectionProbeDown);
        if (!_measurement.IsOpen
            || !string.Equals(_measurement.PortName, _measurementSettings.PortName, StringComparison.OrdinalIgnoreCase)
            || _measurement.BaudRate is { } baud && baud != _measurementSettings.BaudRate)
        {
            await Task.Run(() =>
            {
                _measurement.Close();
                _measurement.Open(_measurementSettings.PortName, _measurementSettings.BaudRate);
            }, cancellationToken);
        }
        _measurement.ResponseTimeout = TimeSpan.FromMilliseconds(_measurementSettings.ResponseTimeoutMilliseconds);
        _measurement.CommandFormat = _measurementSettings.CommandFormat;
        await Io.SetOutputAndWaitAsync(OutputIo.InspectionProbeDown, false, cancellationToken);
    }

    public async Task<HeightSample> MeasureHeightAtAsync(AxisPosition cameraPosition, CancellationToken cancellationToken)
    {
        var position = new AxisPosition
        {
            X = cameraPosition.X + _gantrySettings.ProbeOffsetX,
            Y = cameraPosition.Y + _gantrySettings.ProbeOffsetY,
        };
        await MoveToAsync(position, cancellationToken: cancellationToken);
        await Io.SetOutputAndWaitAsync(OutputIo.InspectionProbeDown, true, cancellationToken);
        var result = await _measurement.MeasureAsync(cancellationToken);
        if (result.Measurements.Count != 1)
            throw new InvalidOperationException(UiText.Get("ML-CP must return one height value"));
        var sample = new HeightSample(position.X, position.Y, (double)result.Measurements[0].Value);
        await Io.SetOutputAndWaitAsync(OutputIo.InspectionProbeDown, false, cancellationToken);
        return sample;
    }

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
            await Task.Run(() =>
            {
                // Restore each connection; camera initialization leaves acquisition stopped.
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
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _visionGate.Release();
        }
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
            throw new InvalidOperationException(UiText.Format($"Record a position for {UiText.Get(pcb)} Data Matrix."));
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

    internal async Task<InspectionCapture> ReadBarcodeAsync(HeatSinkSlot pcb, CancellationToken cancellationToken, PcbAlignment? alignment = null)
    {
        if (!HasBarcodeRegion(pcb))
            throw new InvalidOperationException(UiText.Format($"Teach a FOV and ROI for {UiText.Get(pcb)} Data Matrix."));
        var fov = GetBarcodeFov(pcb);
        var position = _recipes.Current.GetInspectionPosition(fov);
        await MoveToAsync(alignment?.Transform(position) ?? position, cancellationToken: cancellationToken);
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

    public Task MoveToBoltAsync(BoltPoint point, CancellationToken cancellationToken = default, PcbAlignment? alignment = null)
    {
        var position = point.InspectionPosition ?? throw new InvalidOperationException(
            UiText.Format($"Record an inspection position for {UiText.Get(point.HeatSink)} bolt {point.Id}."));
        return MoveToAsync(alignment?.Transform(position) ?? position, cancellationToken: cancellationToken);
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

    internal async Task<InspectionCapture> CaptureBoltAsync(BoltPoint point, CancellationToken cancellationToken = default, PcbAlignment? alignment = null)
    {
        var position = point.InspectionPosition ?? throw new InvalidOperationException(UiText.Get("Not taught"));
        await MoveToAsync(alignment?.Transform(position) ?? position, cancellationToken: cancellationToken).ConfigureAwait(false);
        var image = await CaptureCurrentAsync(cancellationToken,
            lightLevel: point.LightLevel ?? _recipes.Current.BoltInspection.LightLevel).ConfigureAwait(false);
        var capturedAt = DateTimeOffset.Now;
        InspectionCaptured?.Invoke(image, point.HeatSink, point.Id);
        return new(point.Id, capturedAt, image, null, null);
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
                        throw new InvalidOperationException(UiText.Get("Stop jogging before adding a map image."));

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
