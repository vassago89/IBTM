using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFeeder;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningStation : AutoUnit
{
    private readonly BoltFeederUnit _feeder;
    private readonly IXyMotion _motion;
    private readonly BoltFasteningSettings _settings;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly RecipeManager _recipes;
    private readonly UnitSettings _units;
    private readonly ILogger<BoltFasteningStation>? _log;
    private HeatSinkSlot[]? _runTargets;
    // Selected work belongs only to this run; STOP discards it.
    private (BoltPoint Bolt, BoltFasteningStage Stage)[]? _runBolts;
    private int _boltIndex;
    private long _cycleStartedAt;
    private ConveyorStation.Job? _runJob;
    private CancellationTokenSource? _carrierOperation;
    // A supply operation for the next selected bolt, owned only by the current carrier run.
    private (Guid BoltId, CancellationTokenSource Cancellation, Task Completion)? _shootingFeed;

    public BoltFasteningStation(
        IBoltHead shootingHead,
        IBoltHead pickupHead,
        IIoService io,
        IXyMotion motion,
        MotionStatus motionStatus,
        BoltFasteningSettings settings,
        CarrierReferenceSettings carrierReference,
        ConveyorStation station,
        RecipeManager recipes,
        UnitSettings units,
        BoltFeederUnit feeder,
        ILogger<BoltFasteningStation>? log = null)
        : base(io, [
            InputIo.PickupHeadVacuumDetected,
            InputIo.ShootingTubeBoltDetected,
            InputIo.PickupHeadUp,
            InputIo.PickupHeadDown,
            InputIo.ShootingHeadUp,
            InputIo.ShootingHeadDown,
            InputIo.PickupTableUp,
            InputIo.PickupTableDown,
            InputIo.ShootingEscapeForward,
            InputIo.ShootingEscapeBackward,
        ])
    {
        ShootingHead = shootingHead;
        PickupHead = pickupHead;
        _feeder = feeder;
        _motion = motion;
        _settings = settings;
        _carrierReference = carrierReference;
        Station = station;
        _recipes = recipes;
        _units = units;
        _log = log;
        Motion = motionStatus;
        InitializeRecipeBoltPositions();
        recipes.Changed += InitializeRecipeBoltPositions;
        station.Changed += NotifyChanged;
    }

    public IBoltHead ShootingHead { get; }

    public IBoltHead PickupHead { get; }

    public ConveyorStation Station { get; }

    private bool IsReadyToFasten => Station.CarrierSeated && !Station.Completed;

    // Recorded NG/dry-run results count as finished work, not as a passing verdict.
    public bool IsFasteningRecorded
    {
        get
        {
            var targets = Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent).ToArray();
            if (targets.Length == 0)
                return false;
            foreach (var heatSink in targets)
            {
                var bolts = _recipes.Current.Pcb.BoltPoints.Where(bolt => bolt.HeatSink == heatSink).ToArray();
                var assembly = Station.Assemblies.FirstOrDefault(item => item.HeatSink == heatSink);
                if (bolts.Length == 0 || assembly is null
                    || bolts.Any(bolt => (assembly.ShootingBoltResults.GetValueOrDefault(bolt.Id)
                        ?? assembly.PickupBoltResults.GetValueOrDefault(bolt.Id)) is not { IsComplete: true }))
                    return false;
            }
            return true;
        }
    }

    private void InitializeRecipeBoltPositions()
    {
        // Older recipes have only inspection XY. Never overwrite independently taught coordinates.
        foreach (var bolt in _recipes.Current.Pcb.BoltPoints)
            _settings.InitializeBoltPosition(bolt, _carrierReference);
    }

    public MotionStatus Motion { get; }

    public StationCylinderState PickupHeadPosition
    {
        get
        {
            switch ((Io.GetInput(InputIo.PickupHeadUp), Io.GetInput(InputIo.PickupHeadDown)))
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

    public StationCylinderState ShootingHeadPosition
    {
        get
        {
            switch ((Io.GetInput(InputIo.ShootingHeadUp), Io.GetInput(InputIo.ShootingHeadDown)))
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

    public StationCylinderState PickupTablePosition
    {
        get
        {
            switch ((Io.GetInput(InputIo.PickupTableUp), Io.GetInput(InputIo.PickupTableDown)))
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

    public bool IsHorizontalMoveAllowed
    {
        get
        {
            return PickupHeadPosition == StationCylinderState.Up
                && ShootingHeadPosition == StationCylinderState.Up;
        }
    }

    public BoltPoint? ActiveBolt
    {
        get
        {
            var index = _boltIndex;
            return _runBolts is { } bolts && index < bolts.Length ? bolts[index].Bolt : null;
        }
    }

    public BoltFasteningStage? ActiveStage
    {
        get
        {
            var index = _boltIndex;
            return _runBolts is { } bolts && index < bolts.Length ? bolts[index].Stage : null;
        }
    }

    private AxisPosition? StandbyPosition
    {
        get
        {
            if (_settings.FirstFasteningHead == FasteningHead.Pickup)
                return new() { X = _settings.PickupPosition.X, Y = _settings.PickupPosition.Y, Z = 0 };

            var bolt = _recipes.Current.Pcb.GetFasteningPoints(FasteningHead.Shooting)
                .FirstOrDefault(bolt => bolt.Head == FasteningHead.Shooting);
            if (bolt is not { IsFasteningPositionDefined: true })
                return null;
            var position = _settings.GetBoltPosition(bolt);
            position.Z = _settings.SafeZ;
            return position;
        }
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? selectedBolts = null,
        Action<BoltPoint, BoltResult>? resultReceived = null,
        bool continueAfterSelection = false)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        var resumeJob = selectedBolts is null ? null : Station.CurrentJob;
        if (selectedBolts is not null)
        {
            var bolts = _recipes.Current.Pcb.BoltPoints.Where(bolt => selectedBolts.Contains(bolt.Id)).ToArray();
            if (!_units.BoltFastening
                || selectedBolts.Count == 0 && (!IsFasteningRecorded || Station.Completed)
                || bolts.Length != selectedBolts.Distinct().Count())
                throw new InvalidOperationException("Select current recipe bolts to resume fastening.");
            if (!Station.CarrierSeated || bolts.Any(bolt => !Station.IsHeatSinkPresent(bolt.HeatSink)))
                throw new InvalidOperationException("Seat the carrier and load every selected PCB before resuming fastening.");
            if (bolts.Any(bolt => !bolt.IsFasteningPositionDefined))
                throw new InvalidOperationException("Teach the fastening position of every selected bolt before resuming fastening.");
        }
        Exception? failure = null;
        try
        {
            BeginRun();
            var torqueCompensations = new Dictionary<(FasteningHead Head, ushort Preset), ushort>();
            if (_units.BoltFastening)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Read once per run. A resumed run refreshes settings; later carriers reuse this snapshot.
                var runBolts = _recipes.Current.Pcb.BoltPoints.Where(bolt =>
                    selectedBolts is null || continueAfterSelection || selectedBolts.Contains(bolt.Id)).ToArray();
                if (_units.ShootingBoltFeeder && runBolts.Any(bolt => bolt.Head == FasteningHead.Shooting))
                    torqueCompensations[(FasteningHead.Shooting, 1)] =
                        await ShootingHead.ReadTorqueCompensationAsync(1, cancellationToken);
                if (_units.PickupBoltFeeder && runBolts.Any(bolt => bolt.Head == FasteningHead.Pickup))
                {
                    var finalPreset = _settings.PickupFinalPreset;
                    torqueCompensations[(FasteningHead.Pickup, finalPreset)] =
                        await PickupHead.ReadTorqueCompensationAsync(finalPreset, cancellationToken);
                    if (_settings.PickupFasteningMode == PickupFasteningMode.TwoStage
                        && _settings.PickupPreliminaryPreset != finalPreset)
                        torqueCompensations[(FasteningHead.Pickup, _settings.PickupPreliminaryPreset)] =
                            await PickupHead.ReadTorqueCompensationAsync(_settings.PickupPreliminaryPreset, cancellationToken);
                }
                if (_units.ShootingBoltFeeder
                    && (selectedBolts is null || _recipes.Current.Pcb.BoltPoints.Any(
                        bolt => bolt.Head == FasteningHead.Shooting && selectedBolts.Contains(bolt.Id))))
                    Io.SetOutput(OutputIo.ShootingEscapeForward, false);
                if (resumeJob is not null)
                    Station.Restart(resumeJob);
                if (StandbyPosition is { } position)
                {
                    EnterStep(BoltFasteningState.MovingToStandby,
                        $"Startup: Z=0 -> X={position.X}, Y={position.Y} -> Z={position.Z}",
                        Station.CurrentJob.Id);
                    var startupStarted = Stopwatch.GetTimestamp();
                    await RaiseCylindersAsync(cancellationToken);
                    await MoveZAsync(0, cancellationToken);
                    await Io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, false, cancellationToken);
                    EnsureCanMoveHorizontal(cancellationToken);
                    await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, cancellationToken);
                    if (position.Z != 0)
                        await MoveZAsync(position.Z, cancellationToken);
                    _log?.LogInformation("Bolt timing {Job}: startup standby complete, total={ElapsedMs:F1} ms.",
                        Station.CurrentJob.Id, Stopwatch.GetElapsedTime(startupStarted).TotalMilliseconds);
                }
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                if (resumeJob is not null)
                {
                    Station.RequireCurrentJob(resumeJob);
                    if (!Station.CarrierSeated)
                        throw new MotionInterlockException("The fastening carrier is no longer seated.");
                }
                var step = NextStep;
                if (!await ExecuteStepAsync(step, cancellationToken, selectedBolts, resultReceived,
                    continueAfterSelection, torqueCompensations))
                    await WaitForChangeAsync(cancellationToken);
                if (resumeJob is not null && step == BoltFasteningState.CompletingCarrier)
                {
                    if (!continueAfterSelection)
                        break;
                    // The confirmation applies to this carrier only. Later carriers use the full recipe.
                    resumeJob = null;
                    selectedBolts = null;
                    resultReceived = null;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (MaintenanceStopException exception)
        {
            failure = exception;
            // Retries returned to Safe Z, or feed preparation joined its motion.
            try
            {
                await RaiseCylindersAsync(cancellationToken);
                await MoveZAsync(_settings.SafeZ, cancellationToken);
            }
            catch (Exception recoveryFailure) when (
                recoveryFailure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failure = new AggregateException(exception, recoveryFailure);
                throw failure;
            }
            throw;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                await ClearCarrierOperationAsync();
            }
            catch (Exception cleanupFailure)
            {
                if (failure is not null)
                {
                    failure = new AggregateException(failure, cleanupFailure);
                    throw failure;
                }
                failure = cleanupFailure;
                throw;
            }
            finally
            {
                try
                {
                    if (_units.BoltFastening)
                        StopShooting(failure);
                }
                finally
                {
                    EndRun(cancellationToken);
                }
            }
        }
    }

    internal BoltFasteningState NextStep
    {
        get
        {
            if (!_units.BoltFastening)
                return BoltFasteningState.Disabled;
            if (!IsReadyToFasten)
                return BoltFasteningState.Waiting;

            if (_runJob is null)
                return BoltFasteningState.PreparingCarrier;

            var head = ActiveBolt?.Head;
            switch (head)
            {
                case null:
                    return BoltFasteningState.CompletingCarrier;
                case FasteningHead.Shooting or FasteningHead.Pickup:
                    return BoltFasteningState.Fastening;
                default:
                    throw new ArgumentOutOfRangeException(nameof(BoltPoint.Head), head, "Unsupported fastening head.");
            }
        }
    }

    private async Task<bool> ExecuteStepAsync(
        BoltFasteningState step, CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? selectedBolts,
        Action<BoltPoint, BoltResult>? resultReceived, bool continueAfterSelection,
        IReadOnlyDictionary<(FasteningHead Head, ushort Preset), ushort> torqueCompensations)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selectedBolt = ActiveBolt;
        var target = selectedBolt is null ? null
            : $"{selectedBolt.HeatSink}, bolt {_recipes.Current.Pcb.GetBoltOrdinal(selectedBolt.Id)}, {selectedBolt.Head}";
        EnterStep(step, target, Station.CurrentJob.Id);
        var operation = _carrierOperation;
        var job = _runJob!;
        try
        {
            CheckCarrier();
            operation?.Token.ThrowIfCancellationRequested();
            switch (step)
            {
                case BoltFasteningState.Disabled:
                    if (Station.CarrierSeated)
                        Station.Complete();
                    return false;
                case BoltFasteningState.Waiting:
                    return false;
                case BoltFasteningState.PreparingCarrier:
                    await ClearCarrierOperationAsync();
                    _cycleStartedAt = Stopwatch.GetTimestamp();
                    _runTargets = Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent).ToArray();
                    foreach (var heatSink in _runTargets)
                    {
                        if (selectedBolts is null && !_recipes.Current.Pcb.BoltPoints.Any(point => point.HeatSink == heatSink))
                            throw new InvalidOperationException(
                                $"{heatSink.GetDescription()} has no taught bolts. Complete bolt teaching before fastening.");
                    }
                    _runJob = Station.CurrentJob;
                    var selected = _recipes.Current.Pcb.GetFasteningPoints(_settings.FirstFasteningHead)
                        .Where(bolt => _runTargets.Contains(bolt.HeatSink)
                            && (selectedBolts is null || selectedBolts.Contains(bolt.Id)));
                    var work = new List<(BoltPoint Bolt, BoltFasteningStage Stage)>();
                    foreach (var group in selected.GroupBy(bolt => (bolt.Head, bolt.HeatSink)))
                    {
                        var pickupResults = Station.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == group.Key.HeatSink)?.PickupBoltResults;
                        var twoStage = group.Key.Head == FasteningHead.Pickup
                            && _settings.PickupFasteningMode == PickupFasteningMode.TwoStage;
                        foreach (var point in group)
                        {
                            var resumePreliminary = selectedBolts is not null && point.Head == FasteningHead.Pickup
                                && pickupResults?.GetValueOrDefault(point.Id) is { Stage: BoltFasteningStage.Preliminary };
                            if (twoStage)
                            {
                                if (!resumePreliminary)
                                    work.Add((point, BoltFasteningStage.Preliminary));
                            }
                            else
                                work.Add((point, resumePreliminary ? BoltFasteningStage.Final : BoltFasteningStage.Single));
                        }
                        if (twoStage)
                            work.AddRange(group.Reverse().Select(point => (point, BoltFasteningStage.Final)));
                    }
                    _runBolts = work.ToArray();
                    _carrierOperation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Station.Changed += CheckCarrier;
                    CheckCarrier();
                    NotifyChanged();
                    return true;
                case BoltFasteningState.CompletingCarrier:
                {
                    var token = operation?.Token ?? throw new InvalidOperationException("No fastening work is selected.");
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    var completionStarted = Stopwatch.GetTimestamp();
                    await FinishFasteningAsync(FasteningHead.Pickup, token);
                    await FinishFasteningAsync(FasteningHead.Shooting, token);
                    var standby = StandbyPosition;
                    var standbyZ = standby?.Z ?? _settings.SafeZ;
                    var started = Stopwatch.GetTimestamp();
                    await MoveZAsync(standbyZ, token);
                    _log?.LogInformation("Bolt timing {Job}: completion standby Z={Z} arrived, elapsed={ElapsedMs:F1} ms.",
                        job.Id, standbyZ, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    if (standby is not null)
                    {
                        EnterStep(BoltFasteningState.MovingToStandby, $"X={standby.X}, Y={standby.Y}, Z={standby.Z}", job.Id);
                        await Io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, false, token);
                        EnsureCanMoveHorizontal(token);
                        await _motion.MoveToXYAsync(standby.X, standby.Y, _settings.Motion.HorizontalSpeed, token);
                    }
                    token.ThrowIfCancellationRequested();
                    // A partial selection still returns to standby, but cannot release an unfinished carrier.
                    var complete = IsFasteningRecorded;
                    if (!complete && continueAfterSelection)
                        throw new InvalidOperationException("The resumed carrier still has unrecorded bolts.");
                    if (complete)
                    {
                        foreach (var heatSink in _runTargets!)
                            Station.GetAssembly(job, heatSink).CompleteFastening();
                        token.ThrowIfCancellationRequested();
                        Station.Complete(job, Stopwatch.GetElapsedTime(_cycleStartedAt));
                    }
                    await ClearCarrierOperationAsync();
                    _log?.LogInformation("Bolt timing {Job}: carrier completion complete, total={ElapsedMs:F1} ms.",
                        job.Id, Stopwatch.GetElapsedTime(completionStarted).TotalMilliseconds);
                    return true;
                }

                case BoltFasteningState.Fastening:
                {
                    var token = operation?.Token ?? throw new InvalidOperationException("No fastening work is selected.");
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    var bolt = selectedBolt ?? throw new InvalidOperationException("No bolt is selected.");
                    var stage = _runBolts![_boltIndex].Stage;
                    var assembly = Station.GetAssembly(job, bolt.HeatSink);
                    BoltResult? preliminary = null;
                    if (stage == BoltFasteningStage.Final)
                    {
                        preliminary = assembly.PickupBoltResults.GetValueOrDefault(bolt.Id);
                        if (preliminary is not { Stage: BoltFasteningStage.Preliminary })
                            throw new InvalidOperationException("Final tightening requires the recorded pre-tightening result.");
                        if (preliminary.IsComplete)
                        {
                            // A failed pre-tightening result stays NG; never feed or tighten this bolt again.
                            _boltIndex++;
                            NotifyChanged();
                            return true;
                        }
                    }
                    var continuingFinal = stage == BoltFasteningStage.Final && _boltIndex > 0
                        && _runBolts[_boltIndex - 1] is { Stage: BoltFasteningStage.Preliminary, Bolt: var previousBolt }
                        && previousBolt.Id == bolt.Id;
                    var cycleStarted = Stopwatch.GetTimestamp();
                    _log?.LogInformation("Bolt timing {Job}/{Bolt}: begin, PCB={Pcb}, head={Head}, safe Z={SafeZ}.",
                        job.Id, bolt.Id, bolt.HeatSink, bolt.Head, _settings.GetSafeZ(bolt.Head));
                    var feeding = _units.IsBoltFeederEnabled(bolt.Head);
                    switch (bolt.Head)
                    {
                        case FasteningHead.Shooting:
                        {
                            TraceStep(step, target, job.Id, "head/table clearance, shooting feed and point movement");
                            if (_shootingFeed is not null)
                            {
                                // Do not release a supplied bolt by repeating vacuum-OFF clearance.
                                if (!IsHorizontalMoveAllowed || PickupTablePosition != StationCylinderState.Up)
                                    throw new MotionInterlockException(
                                        "Head/table clearance was lost after starting supply for the next shooting bolt.");
                            }
                            else
                            {
                                if (PickupTablePosition != StationCylinderState.Up)
                                {
                                    await RaiseCylindersAsync(token);
                                    await MoveZAsync(_settings.SafeZ, token);
                                    await Io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, false, token);
                                }
                                if (ShootingHeadPosition != StationCylinderState.Up)
                                    await ClearHeadAsync(FasteningHead.Shooting, _settings.GetSafeZ(FasteningHead.Shooting), token);
                            }
                            await RaiseCylindersAsync(token);

                            if (feeding)
                            {
                                var pendingFeed = _shootingFeed;
                                if (pendingFeed is { } pending && pending.BoltId != bolt.Id)
                                    throw new InvalidOperationException("The shooting supply belongs to a different bolt.");
                                _shootingFeed = null;
                                using var pendingCancellation = pendingFeed?.Cancellation;
                                using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
                                var shooting = pendingFeed?.Completion;
                                if (shooting?.IsCompleted == true)
                                    await shooting;
                                // XY travel and fastening-Z descent overlap the supply's arrival delay.
                                var moving = MoveToBoltAsync(bolt, preparation.Token);
                                if (moving.IsCompleted && pendingFeed is null)
                                    await moving;
                                shooting ??= ShootBoltAsync(preparation.Token, bolt.Id);
                                var supplyWaitStarted = Stopwatch.GetTimestamp();
                                if (pendingFeed is not null)
                                    _log?.LogInformation("Bolt timing {Bolt}: using shooting supply started during previous retraction.", bolt.Id);
                                var first = await Task.WhenAny(moving, shooting);
                                if (!first.IsCompletedSuccessfully
                                    && first.Exception?.InnerException is not MaintenanceStopException)
                                {
                                    preparation.Cancel();
                                    pendingCancellation?.Cancel();
                                }
                                // Drain both operations, including STOP/air-OFF cleanup on failure.
                                var preparationCompleted = Task.WhenAll(moving, shooting);
                                try
                                {
                                    await preparationCompleted;
                                }
                                catch when (preparationCompleted.Exception is { InnerExceptions.Count: > 1 } failures)
                                {
                                    throw failures;
                                }
                                _log?.LogInformation("Bolt timing {Bolt}: movement / shooting supply joined, elapsed={ElapsedMs:F1} ms, supplied during previous retraction={Prefed}.",
                                    bolt.Id, Stopwatch.GetElapsedTime(supplyWaitStarted).TotalMilliseconds, pendingFeed is not null);
                            }
                            else
                                await MoveToBoltAsync(bolt, token);
                            break;
                        }
                        case FasteningHead.Pickup:
                        {
                            if (continuingFinal)
                            {
                                // Reuse this bolt's XYZ position; raise the cylinder again before START.
                                // Check live position/clearance instead of assuming the last command still holds.
                                const double PositionToleranceMillimeters = 0.05;
                                var position = _settings.GetBoltPosition(bolt);
                                var current = _motion.Position;
                                if (!MotionServiceBase.IsReadyAndStopped(_motion)
                                    || ShootingHeadPosition != StationCylinderState.Up
                                    || PickupTablePosition != StationCylinderState.Down
                                    || !(Math.Abs(current.X - position.X) <= PositionToleranceMillimeters
                                        && Math.Abs(current.Y - position.Y) <= PositionToleranceMillimeters
                                        && Math.Abs(current.Z - position.Z) <= PositionToleranceMillimeters))
                                    throw new MotionInterlockException(UiText.Get("Final tightening position is not ready."));
                                await SetVacuumAsync(FasteningHead.Pickup, false, token);
                                break;
                            }
                            if (feeding && stage != BoltFasteningStage.Final
                                && _feeder.PickupEmptyAlarm is { } pickupAlarm
                                && !Io.GetInput(InputIo.PickupHeadVacuumDetected))
                                throw new MaintenanceStopException(
                                    "Pickup bolt supply stopped. Refill the feeder and RESET before resuming.", pickupAlarm);
                            if (ShootingHeadPosition != StationCylinderState.Up)
                                await ClearHeadAsync(FasteningHead.Shooting, _settings.GetSafeZ(FasteningHead.Shooting), token);
                            if (PickupTablePosition != StationCylinderState.Down)
                            {
                                var started = Stopwatch.GetTimestamp();
                                await RaiseCylindersAsync(token);
                                _log?.LogInformation("Bolt timing {Bolt}: pickup changeover heads UP confirmed, elapsed={ElapsedMs:F1} ms.",
                                    bolt.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                                started = Stopwatch.GetTimestamp();
                                await MoveZAsync(_settings.SafeZ, token);
                                _log?.LogInformation("Bolt timing {Bolt}: pickup changeover Safe Z={Z} arrived, elapsed={ElapsedMs:F1} ms.",
                                    bolt.Id, _settings.SafeZ, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                                started = Stopwatch.GetTimestamp();
                                await Io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, true, token);
                                _log?.LogInformation("Bolt timing {Bolt}: pickup table DOWN confirmed, elapsed={ElapsedMs:F1} ms.",
                                    bolt.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                            }

                            if (stage == BoltFasteningStage.Final)
                            {
                                // The screw is already seated; final tightening needs no pickup vacuum.
                                await SetVacuumAsync(FasteningHead.Pickup, false, token);
                                await RaiseCylindersAsync(token);
                                await MoveZAsync(_settings.SafeZ, token);
                            }
                            else if (!Io.GetInput(InputIo.PickupHeadVacuumDetected))
                            {
                                TraceStep(step, target, job.Id, "pickup XY movement");
                                await MoveToPickupXYAsync(token);
                                var retryCount = _settings.PickupRetryCount;
                                for (var retry = 0; ; retry++)
                                {
                                    token.ThrowIfCancellationRequested();
                                    if (feeding)
                                    {
                                        TraceStep(step, target, job.Id, $"pickup attempt {retry + 1}: waiting for feeder bolt detection");
                                        var started = Stopwatch.GetTimestamp();
                                        await WaitForBoltSupplyAsync(FasteningHead.Pickup, token);
                                        _log?.LogInformation("Bolt timing {Bolt}: pickup feeder detected, attempt={Attempt}, elapsed={ElapsedMs:F1} ms.",
                                            bolt.Id, retry + 1, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                                    }
                                    TraceStep(step, target, job.Id, $"pickup attempt {retry + 1}: descent");
                                    await MoveToPickupZAsync(token);

                                    if (feeding)
                                    {
                                        token.ThrowIfCancellationRequested();
                                        Io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
                                        await Task.Delay(_settings.PickupVacuumDelayMilliseconds, token);
                                    }

                                    TraceStep(step, target, job.Id, $"pickup attempt {retry + 1}: return to Safe Z");
                                    await ReturnFromPickupAsync(token);
                                    token.ThrowIfCancellationRequested();
                                    if (!feeding)
                                        break;
                                    var vacuumDetected = Io.GetInput(InputIo.PickupHeadVacuumDetected);
                                    _log?.LogInformation("Bolt timing {Bolt}: pickup vacuum at Safe Z, attempt={Attempt}, detected={Detected}.",
                                        bolt.Id, retry + 1, vacuumDetected);
                                    if (vacuumDetected)
                                        break;
                                    Io.SetOutput(OutputIo.PickupHeadVacuumPump, false);
                                    if (retry >= retryCount)
                                        throw new MaintenanceStopException(
                                            $"Pickup bolt {bolt.Id}, {bolt.HeatSink}: vacuum not detected at Safe Z after {retry + 1} pickup attempts.");
                                    _log?.LogWarning(
                                        "Pickup bolt {Bolt}, {HeatSink}: vacuum not detected at Safe Z; retry {Retry}/{RetryCount}.",
                                        bolt.Id, bolt.HeatSink, retry + 1, retryCount);
                                }
                                token.ThrowIfCancellationRequested();
                                Station.RequireCurrentJob(job);
                            }
                            else
                                await ReturnFromPickupAsync(token);
                            TraceStep(step, target, job.Id, "fastening point movement");
                            await RaiseCylindersAsync(token);
                            await MoveToBoltAsync(bolt, token);
                            break;
                        }
                        default:
                            throw new ArgumentOutOfRangeException(nameof(bolt.Head));
                    }
                    _log?.LogInformation("Bolt timing {Job}/{Bolt}: preparation ready, elapsed={ElapsedMs:F1} ms, feeding={Feeding}.",
                        job.Id, bolt.Id, Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds, feeding);
                    TraceStep(step, target, job.Id, "fastening controller result");
                    BoltResult? result = null;
                    Exception? fasteningFailure = null;
                    double? minimumTurns;
                    double? maximumTurns;
                    lock (_recipes.InspectionSync)
                    {
                        minimumTurns = stage == BoltFasteningStage.Preliminary ? null : bolt.MinimumTurns;
                        maximumTurns = stage == BoltFasteningStage.Preliminary ? null : bolt.MaximumTurns;
                    }
                    try
                    {
                        var head = bolt.Head switch
                        {
                            FasteningHead.Shooting => ShootingHead,
                            FasteningHead.Pickup => PickupHead,
                            _ => throw new ArgumentOutOfRangeException(nameof(bolt.Head)),
                        };
                        var started = Stopwatch.GetTimestamp();
                        var preset = bolt.Head == FasteningHead.Shooting ? (ushort)1
                            : stage == BoltFasteningStage.Preliminary
                                ? _settings.PickupPreliminaryPreset : _settings.PickupFinalPreset;
                        await head.SelectPresetAsync(preset, token);
                        _log?.LogInformation("Bolt timing {Bolt}: preset selection, elapsed={ElapsedMs:F1} ms.",
                            bolt.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        started = Stopwatch.GetTimestamp();
                        await RaiseCylindersAsync(token);
                        _log?.LogInformation("Bolt timing {Bolt}: heads UP confirmed before START, elapsed={ElapsedMs:F1} ms.",
                            bolt.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        Station.RequireCurrentJob(job);

                        using (var fastening = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            void CheckFasteningFeedback()
                            {
                                if (bolt.Head == FasteningHead.Shooting
                                        && PickupTablePosition != StationCylinderState.Up
                                    || stage == BoltFasteningStage.Final
                                        && (PickupTablePosition != StationCylinderState.Down
                                            || ShootingHeadPosition != StationCylinderState.Up))
                                    OperationCancellation.CancelIfNotDisposed(fastening);
                            }

                            Task LowerHeadWhileFasteningAsync(CancellationToken feedToken)
                            {
                                feedToken.ThrowIfCancellationRequested();
                                _log?.LogInformation("Bolt {Head}: motor START completed; requesting head DOWN.", bolt.Head);
                                // Screw contact can stop the cylinder before its DOWN sensor.
                                Io.SetOutput(bolt.Head == FasteningHead.Pickup
                                    ? OutputIo.PickupHeadDown : OutputIo.ShootingHeadDown, true);
                                feedToken.ThrowIfCancellationRequested();
                                _log?.LogInformation("Bolt {Head}: head DOWN output sent.", bolt.Head);
                                return Task.CompletedTask;
                            }

                            Changed += CheckFasteningFeedback;
                            try
                            {
                                CheckFasteningFeedback();
                                var dryRunMilliseconds = !_units.IsBoltFeederEnabled(bolt.Head)
                                    ? _settings.DryRunMilliseconds : 0;
                                _log?.LogInformation(
                                    "Bolt {Head}, {HeatSink}, point {Bolt}: starting {Controller}; dry run={DryRunMilliseconds} ms (0=wait for fastening result).",
                                    bolt.Head, bolt.HeatSink, bolt.Id, head.GetType().Name, dryRunMilliseconds);
                                started = Stopwatch.GetTimestamp();
                                var completed = await head.TightenAsync(
                                    fastening.Token,
                                    LowerHeadWhileFasteningAsync,
                                    dryRunMilliseconds, received => result = received,
                                    torqueCompensations.TryGetValue((bolt.Head, preset), out var compensation) ? compensation : null);
                                _log?.LogInformation("Bolt timing {Bolt}: controller START/result/STOP, elapsed={ElapsedMs:F1} ms, controller time={ControllerMs} ms.",
                                    bolt.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds, completed.Controller?.FasteningTimeMilliseconds);
                                completed = completed with { RecordedAt = completed.RecordedAt ?? DateTimeOffset.Now };
                                _log?.LogInformation(
                                    "Bolt {Head}, {HeatSink}, point {Bolt}: cycle completed; success={Success}, source={Source}, error={Error}.",
                                    bolt.Head, bolt.HeatSink, bolt.Id, completed.Success, completed.Source, completed.Error);
                                Station.RequireCurrentJob(job);
                                result = completed;
                            }
                            catch (OperationCanceledException) when (fastening.IsCancellationRequested && !token.IsCancellationRequested)
                            {
                                throw new MotionInterlockException(stage == BoltFasteningStage.Final
                                    ? UiText.Get("Final tightening head or table feedback was lost.")
                                    : "Keep the pickup table raised during shooting fastening.");
                            }
                            finally
                            {
                                Changed -= CheckFasteningFeedback;
                            }
                        }

                        var nextBolt = _boltIndex + 1 < _runBolts!.Length ? _runBolts[_boltIndex + 1].Bolt : null;
                        if (stage == BoltFasteningStage.Preliminary
                            && result is { Success: true, Source: not BoltResultSource.DryRun }
                            && nextBolt?.Id == bolt.Id
                            && _runBolts[_boltIndex + 1].Stage == BoltFasteningStage.Final)
                        {
                            TraceStep(step, target, job.Id, "vacuum OFF; final tightening at the same bolt");
                            await SetVacuumAsync(FasteningHead.Pickup, false, token);
                        }
                        else
                        {
                            // Clear before publishing unless the same bolt immediately continues to final tightening.
                            TraceStep(step, target, job.Id, "head retraction");
                            var nextShootingBolt = bolt.Head == FasteningHead.Shooting && feeding
                                && nextBolt is { Head: FasteningHead.Shooting } ? nextBolt.Id : (Guid?)null;
                            var safeZ = bolt.Head == FasteningHead.Shooting && nextBolt is { Head: FasteningHead.Pickup }
                                ? _settings.SafeZ : _settings.GetSafeZ(bolt.Head);
                            await ClearHeadAsync(bolt.Head, safeZ, token, nextShootingBolt);
                        }
                    }
                    catch (Exception exception)
                    {
                        fasteningFailure = exception;
                        throw;
                    }
                    finally
                    {
                        // Keep the measured result with its original carrier even if STOP or clearance
                        // fails. A storage failure must not hide the original hardware failure.
                        try
                        {
                            if (result is not null)
                            {
                                result = result with
                                {
                                    Stage = stage,
                                    PreliminaryResult = preliminary,
                                    MinimumTurns = minimumTurns,
                                    MaximumTurns = maximumTurns,
                                };
                                var recordStarted = Stopwatch.GetTimestamp();
                                assembly.RecordBolt(bolt.Head, bolt.Id, result);
                                resultReceived?.Invoke(bolt, result);
                                _log?.LogInformation("Bolt {Bolt}: controller OK={Success}, turns={Turns}, minimum={MinimumTurns}, maximum={MaximumTurns}, turns result={TurnsResult}.",
                                    bolt.Id, result.Success, result.TotalTurns, result.MinimumTurns, result.MaximumTurns, result.TurnsResult);
                                _log?.LogInformation("Bolt timing {Job}/{Bolt}: result published, elapsed={ElapsedMs:F1} ms.",
                                    job.Id, bolt.Id, Stopwatch.GetElapsedTime(recordStarted).TotalMilliseconds);
                            }
                        }
                        catch (Exception recordFailure) when (fasteningFailure is not null)
                        {
                            throw new AggregateException(fasteningFailure, recordFailure);
                        }
                    }

                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    _boltIndex++;
                    _log?.LogInformation("Bolt timing {Job}/{Bolt}: finished, total={ElapsedMs:F1} ms, success={Success}.",
                        job.Id, bolt.Id, Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds, result?.Success);
                    NotifyChanged();
                    return true;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(step));
            }
        }
        catch (OperationCanceledException) when (operation?.IsCancellationRequested == true
            && !cancellationToken.IsCancellationRequested)
        {
            throw new MotionInterlockException(
                "The fastening carrier changed or lost its seated / PCB presence feedback. Check the carrier before restarting.");
        }
    }

    private void CheckCarrier()
    {
        if (_carrierOperation is not { } operation)
            return;
        lock (operation)
        {
            if (ReferenceEquals(operation, _carrierOperation)
                && (!Station.CarrierSeated
                    || !ReferenceEquals(_runJob, Station.CurrentJob)
                    || Enum.GetValues<HeatSinkSlot>().Any(heatSink =>
                        Station.IsHeatSinkPresent(heatSink) != _runTargets!.Contains(heatSink))))
                operation.Cancel();
        }
    }

    private async Task ClearCarrierOperationAsync()
    {
        Station.Changed -= CheckCarrier;
        var operation = _carrierOperation;
        var pendingFeed = _shootingFeed;
        _shootingFeed = null;
        try
        {
            if (operation is not null)
            {
                lock (operation)
                {
                    _carrierOperation = null;
                    operation.Cancel();
                }
            }
            if (pendingFeed is { } pending)
            {
                pending.Cancellation.Cancel();
                try
                {
                    await pending.Completion;
                }
                catch (OperationCanceledException) when (pending.Cancellation.IsCancellationRequested) { }
            }
        }
        finally
        {
            pendingFeed?.Cancellation.Dispose();
            operation?.Dispose();
            _runJob = null;
            _runTargets = null;
            _runBolts = null;
            _boltIndex = 0;
        }
    }

    internal async Task ShootBoltAsync(CancellationToken cancellationToken = default, Guid? boltId = null)
    {
        var started = Stopwatch.GetTimestamp();
        boltId ??= ActiveBolt?.Id;
        _log?.LogInformation("Bolt timing {Bolt}: shooting feed begin.", boltId);
        cancellationToken.ThrowIfCancellationRequested();
        if ((Io.GetInput(InputIo.ShootingEscapeForward), Io.GetInput(InputIo.ShootingEscapeBackward))
            is not (false, true))
            await Io.SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, false, cancellationToken);
        await WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken);
        await Io.WaitForInputAsync(
            InputIo.ShootingTubeBoltDetected, false, _settings.ShootingDetectionTimeoutMilliseconds,
            cancellationToken, requireCurrent: true);
        _log?.LogInformation("Bolt timing {Bolt}: feeder ready / tube clear, elapsed={ElapsedMs:F1} ms.",
            boltId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        using var passage = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? boltPassed = null;
        Exception? failure = null;
        try
        {
            started = Stopwatch.GetTimestamp();
            await Io.SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, true, cancellationToken);
            _log?.LogInformation("Bolt timing {Bolt}: escape FORWARD, elapsed={ElapsedMs:F1} ms.",
                boltId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            started = Stopwatch.GetTimestamp();
            Io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
            boltPassed = Io.WaitForInputAsync(
                InputIo.ShootingTubeBoltDetected, true, _settings.ShootingDetectionTimeoutMilliseconds, passage.Token);
            cancellationToken.ThrowIfCancellationRequested();
            Io.SetOutput(OutputIo.ShootBolt, true);
            await boltPassed;
            _log?.LogInformation("Bolt timing {Bolt}: shot to tube ON, elapsed={ElapsedMs:F1} ms.",
                boltId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var arrivalStartedAt = Stopwatch.GetTimestamp();
            await Io.WaitForInputAsync(
                InputIo.ShootingTubeBoltDetected, false, _settings.ShootingDetectionTimeoutMilliseconds, cancellationToken);
            _log?.LogInformation("Bolt timing {Bolt}: tube passage OFF, elapsed={ElapsedMs:F1} ms.",
                boltId, Stopwatch.GetElapsedTime(arrivalStartedAt).TotalMilliseconds);
            started = Stopwatch.GetTimestamp();
            cancellationToken.ThrowIfCancellationRequested();
            Io.SetOutput(OutputIo.ShootingEscapeForward, false);
            _log?.LogInformation("Bolt timing {Bolt}: escape BACKWARD requested, elapsed={ElapsedMs:F1} ms.",
                boltId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var arrivalRemaining = TimeSpan.FromSeconds(_settings.ShootingArrivalDelaySeconds)
                - Stopwatch.GetElapsedTime(arrivalStartedAt);
            _log?.LogInformation("Bolt timing {Bolt}: remaining arrival wait={RemainingMs:F1} ms, configured={DelaySeconds} s.",
                boltId, Math.Max(0, arrivalRemaining.TotalMilliseconds), _settings.ShootingArrivalDelaySeconds);
            if (arrivalRemaining > TimeSpan.Zero)
                await Task.Delay(arrivalRemaining, cancellationToken);
            _log?.LogInformation("Bolt timing {Bolt}: shooting feed ready.", boltId);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                StopShooting(failure);
            }
            finally
            {
                passage.Cancel();
                if (boltPassed is not null)
                    await boltPassed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    internal async Task FinishFasteningAsync(
        FasteningHead head,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        _log?.LogInformation("Bolt {Head}: requesting vacuum OFF.", head);
        await SetVacuumAsync(head, false, cancellationToken);
        _log?.LogInformation("Bolt timing {Head}: vacuum OFF request completed, elapsed={ElapsedMs:F1} ms; requesting head UP.",
            head, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        started = Stopwatch.GetTimestamp();
        await SetHeadDownAsync(head, false, cancellationToken);
        _log?.LogInformation("Bolt timing {Head}: head UP confirmed, elapsed={ElapsedMs:F1} ms.",
            head, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private async Task ClearHeadAsync(
        FasteningHead head, double safeZ, CancellationToken cancellationToken, Guid? nextShootingBolt = null)
    {
        var boltId = ActiveBolt?.Id;
        var clearanceStarted = Stopwatch.GetTimestamp();
        await SetVacuumAsync(head, false, cancellationToken);
        _log?.LogInformation("Bolt timing {Bolt}/{Head}: vacuum OFF request completed, elapsed={ElapsedMs:F1} ms.",
            boltId, head, Stopwatch.GetElapsedTime(clearanceStarted).TotalMilliseconds);

        using var clearance = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var zStarted = Stopwatch.GetTimestamp();
        _log?.LogInformation("Bolt timing {Bolt}/{Head}: parallel head UP / retract Z={SafeZ} begin.",
            boltId, head, safeZ);
        var moving = MoveZAsync(safeZ, clearance.Token);
        if (moving.IsCompleted)
            await moving;
        var headStarted = Stopwatch.GetTimestamp();
        var raising = RaiseCylindersAsync(clearance.Token);
        if (nextShootingBolt is { } nextId && !raising.IsFaulted && !raising.IsCanceled)
        {
            _log?.LogInformation("Bolt timing {Bolt}: starting shooting supply for next bolt {NextBolt} during head UP / Z retraction.",
                boltId, nextId);
            var supplyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _shootingFeed = (nextId, supplyCancellation, ShootBoltAsync(supplyCancellation.Token, nextId));
        }
        var first = await Task.WhenAny(moving, raising);
        var firstFinished = Stopwatch.GetTimestamp();
        if (!first.IsCompletedSuccessfully)
            clearance.Cancel();
        // No next XY move or result publication until both operations have finished.
        var clearanceCompleted = Task.WhenAll(moving, raising);
        try
        {
            await clearanceCompleted;
        }
        catch when (clearanceCompleted.Exception is { InnerExceptions.Count: > 1 } failures)
        {
            throw failures;
        }
        var finished = Stopwatch.GetTimestamp();
        _log?.LogInformation(
            "Bolt timing {Bolt}/{Head}: clearance complete; head UP={HeadMs:F1} ms, retract Z={SafeZ} in {ZMs:F1} ms, parallel={ParallelMs:F1} ms, total including vacuum OFF={TotalMs:F1} ms.",
            boltId, head,
            Stopwatch.GetElapsedTime(headStarted, first == raising ? firstFinished : finished).TotalMilliseconds,
            safeZ, Stopwatch.GetElapsedTime(zStarted, first == moving ? firstFinished : finished).TotalMilliseconds,
            Stopwatch.GetElapsedTime(zStarted, finished).TotalMilliseconds,
            Stopwatch.GetElapsedTime(clearanceStarted, finished).TotalMilliseconds);
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ShootingHead.CheckReadyAsync(cancellationToken);
        await PickupHead.CheckReadyAsync(cancellationToken);
    }

    public async Task ResetHeadsAsync(CancellationToken cancellationToken = default)
    {
        List<Exception>? failures = null;
        foreach (var head in new[] { ShootingHead, PickupHead })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await head.ResetAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Bolt controller reset failed.", failures);
        }
    }

    public Task SetHeadDownAsync(
        FasteningHead head,
        bool down,
        CancellationToken cancellationToken = default)
    {
        var output = head switch
        {
            FasteningHead.Pickup => OutputIo.PickupHeadDown,
            FasteningHead.Shooting => OutputIo.ShootingHeadDown,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        return Io.SetOutputAndWaitAsync(output, down, cancellationToken);
    }

    public async Task RaiseCylindersAsync(CancellationToken cancellationToken = default)
    {
        var raising = Task.WhenAll(
            SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken),
            SetHeadDownAsync(FasteningHead.Shooting, false, cancellationToken));
        try
        {
            await raising;
        }
        catch when (raising.Exception is { InnerExceptions.Count: > 1 } failures)
        {
            throw failures;
        }
    }

    internal async Task WaitForBoltSupplyAsync(FasteningHead head, CancellationToken cancellationToken)
    {
        var boltDetected = head switch
        {
            FasteningHead.Pickup => InputIo.PickupFeederBoltDetected,
            FasteningHead.Shooting => InputIo.ShootingFeederBoltDetected,
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        var changed = new AsyncAutoResetEvent();
        void OnBoltInputChanged(InputIo input, bool value)
        {
            if (input == boltDetected)
                changed.Set();
        }
        _feeder.Changed += changed.Set;
        Io.InputChanged += OnBoltInputChanged;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (true)
            {
                var detected = Io.GetInput(boltDetected);
                cancellationToken.ThrowIfCancellationRequested();
                var alarm = head == FasteningHead.Pickup
                    ? _feeder.PickupEmptyAlarm : _feeder.ShootingEmptyAlarm;
                // A ready shooting supply can finish; a new pickup still requires RESET after its alarm.
                if (alarm is not null && (head == FasteningHead.Pickup || !detected))
                    throw new MaintenanceStopException(
                        "Bolt supply stopped. Refill the feeder and RESET before resuming.", alarm);
                if (detected)
                    return;
                await changed.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            _feeder.Changed -= changed.Set;
            Io.InputChanged -= OnBoltInputChanged;
        }
    }

    public void StopShooting(Exception? operationFailure = null)
    {
        Exception? cleanupFailure = null;
        try
        {
            Io.SetOutput(OutputIo.ShootBolt, false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }
        try
        {
            Io.SetOutput(OutputIo.ShootingEscapeForward, false);
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure is null ? exception : new AggregateException(cleanupFailure, exception);
        }
        if (cleanupFailure is not null)
            throw operationFailure is null ? cleanupFailure : new AggregateException(operationFailure, cleanupFailure);
    }

    public void StopIoStart(FasteningHead head)
    {
        Io.SetOutput(
            head == FasteningHead.Pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart,
            false);
    }

    public async Task SetVacuumAsync(
        FasteningHead head,
        bool on,
        CancellationToken cancellationToken)
    {
        var output = head == FasteningHead.Pickup
            ? OutputIo.PickupHeadVacuumPump
            : OutputIo.ShootingHeadVacuumPump;
        cancellationToken.ThrowIfCancellationRequested();
        Io.SetOutput(output, on);
        if (head == FasteningHead.Pickup)
            await Io.WaitForInputAsync(InputIo.PickupHeadVacuumDetected, on, cancellationToken, requireCurrent: true);
    }

    public async Task<bool> HomeAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        if (axis != MotionAxis.Z)
            EnsureCanMoveHorizontal(cancellationToken);
        return await _motion.HomeAsync(axis, _settings.Motion.Home(axis).SearchSpeed, cancellationToken);
    }

    public async Task<bool> HomeHorizontalAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return await _motion.HomeHorizontalAsync(
            _settings.Motion.HorizontalHome.SearchSpeed,
            cancellationToken);
    }

    public async Task MoveToXYAsync(double x, double y, CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        await MoveZAsync(_settings.SafeZ, cancellationToken);
        EnsureCanMoveHorizontal(cancellationToken);
        await _motion.MoveToXYAsync(x, y, _settings.Motion.HorizontalSpeed, cancellationToken);
    }

    public Task MoveZAsync(double z, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_motion.GetAxisState(MotionAxis.Z).Homed)
            throw new MotionInterlockException("Home fastening Z before moving to a taught height.");
        return _motion.MoveAxisAsync(MotionAxis.Z, z, _settings.Motion.ZSpeed, cancellationToken);
    }

    public async Task ReturnFromPickupAsync(CancellationToken cancellationToken = default)
    {
        var boltId = ActiveBolt?.Id;
        var returnStarted = Stopwatch.GetTimestamp();
        await MoveZAsync(_settings.SafeZ, cancellationToken);
        await SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken);
        _log?.LogInformation("Bolt timing {Bolt}: return from feeder to common Safe Z, elapsed={ElapsedMs:F1} ms.",
            boltId, Stopwatch.GetElapsedTime(returnStarted).TotalMilliseconds);
    }

    public async Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default)
    {
        switch (point)
        {
            case { Target: TeachingTarget.BoltPosition, Bolt: not null }:
            case { Target: TeachingTarget.BoltPickup }:
            {
                var pickup = point.Target == TeachingTarget.BoltPickup;
                var head = pickup ? FasteningHead.Pickup : point.Bolt!.Head;
                _log?.LogInformation(
                    "Bolt teaching Move To: {Target}, bolt {Bolt}, {Head}; target X={X}, Y={Y}, Z={Z}; Safe Z={SafeZ}.",
                    point.Target, point.Bolt?.Id, head, position.X, position.Y, position.Z, _settings.SafeZ);
                if (!point.HasPosition)
                    throw new MotionInterlockException("Record the teaching position before moving.");
                var tableDown = head == FasteningHead.Pickup;
                if (pickup)
                    await RaiseCylindersAsync(cancellationToken);
                EnsureCanMoveHorizontal(cancellationToken);
                await MoveZAsync(_settings.SafeZ, cancellationToken);
                _log?.LogInformation("Bolt teaching Move To: Safe Z completed; requesting pickup table {Table}.",
                    tableDown ? "DOWN" : "UP");
                EnsureCanMoveHorizontal(cancellationToken);
                if (PickupTablePosition != (tableDown ? StationCylinderState.Down : StationCylinderState.Up))
                    await Io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, tableDown, cancellationToken);
                _log?.LogInformation("Bolt teaching Move To: pickup table feedback confirmed.");
                using var move = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                void CheckTeachingClearance()
                {
                    if (!IsHorizontalMoveAllowed
                        || PickupTablePosition != (tableDown ? StationCylinderState.Down : StationCylinderState.Up))
                        OperationCancellation.CancelIfNotDisposed(move);
                }

                Changed += CheckTeachingClearance;
                try
                {
                    CheckTeachingClearance();
                    EnsureCanMoveHorizontal(move.Token);
                    _log?.LogInformation("Bolt teaching Move To: requesting XY, X={X}, Y={Y}.", position.X, position.Y);
                    await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, move.Token);
                    _log?.LogInformation("Bolt teaching Move To: XY command completed; requesting target Z={Z}.", position.Z);
                    CheckTeachingClearance();
                    EnsureCanMoveHorizontal(move.Token);
                    await MoveZAsync(position.Z, move.Token);
                    move.Token.ThrowIfCancellationRequested();
                    _log?.LogInformation("Bolt teaching Move To: target Z completed.");
                }
                catch (OperationCanceledException) when (move.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new MotionInterlockException(
                        $"Keep both fastening heads up and the pickup table {(tableDown ? "down" : "up")} while moving to the teaching position.");
                }
                finally
                {
                    Changed -= CheckTeachingClearance;
                }
                break;
            }
            case { Mode: TeachMode.XYOnly }:
                await MoveToXYAsync(position.X, position.Y, cancellationToken);
                break;
            case { Mode: TeachMode.ZOnly }:
                await MoveZAsync(position.Z, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    public Task AdjustAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        return _motion.AdjustAxisAsync(axis, position, velocity, cancellationToken);
    }

    public Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        return _motion.JogAsync(axis, velocity, cancellationToken);
    }

    internal async Task MoveToBoltAsync(
        BoltPoint bolt, CancellationToken cancellationToken = default)
    {
        var position = _settings.GetBoltPosition(bolt);
        _log?.LogInformation(
            "Automatic bolt move: {HeatSink}, bolt {Bolt}, {Head}; target X={X}, Y={Y}, Z={Z}.",
            bolt.HeatSink, bolt.Id, bolt.Head, position.X, position.Y, position.Z);
        // Heads must be raised before travelling at this head's clearance height.
        var safeZ = _settings.GetSafeZ(bolt.Head);
        var started = Stopwatch.GetTimestamp();
        EnsureCanMoveHorizontal(cancellationToken);
        await MoveZAsync(safeZ, cancellationToken);
        _log?.LogInformation("Bolt timing {Bolt}: travel Z={SafeZ}, elapsed={ElapsedMs:F1} ms.",
            bolt.Id, safeZ, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        started = Stopwatch.GetTimestamp();
        EnsureCanMoveHorizontal(cancellationToken);
        await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, cancellationToken);
        _log?.LogInformation("Bolt timing {Bolt}: XY move, elapsed={ElapsedMs:F1} ms.",
            bolt.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        started = Stopwatch.GetTimestamp();
        EnsureCanMoveHorizontal(cancellationToken);
        await MoveZAsync(position.Z, cancellationToken);
        _log?.LogInformation("Bolt timing {Bolt}: fastening Z={Z}, elapsed={ElapsedMs:F1} ms.",
            bolt.Id, position.Z, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    internal async Task MoveToPickupXYAsync(CancellationToken cancellationToken = default)
    {
        var boltId = ActiveBolt?.Id;
        var started = Stopwatch.GetTimestamp();
        await RaiseCylindersAsync(cancellationToken);
        _log?.LogInformation("Bolt timing {Bolt}: pickup travel heads UP confirmed, elapsed={ElapsedMs:F1} ms.",
            boltId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        started = Stopwatch.GetTimestamp();
        await MoveZAsync(_settings.SafeZ, cancellationToken);
        _log?.LogInformation("Bolt timing {Bolt}: pickup travel Safe Z={Z} arrived, elapsed={ElapsedMs:F1} ms.",
            boltId, _settings.SafeZ, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (PickupTablePosition != StationCylinderState.Down)
        {
            started = Stopwatch.GetTimestamp();
            await Io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, true, cancellationToken);
            _log?.LogInformation("Bolt timing {Bolt}: pickup travel table DOWN confirmed, elapsed={ElapsedMs:F1} ms.",
                boltId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        started = Stopwatch.GetTimestamp();
        EnsureCanMoveHorizontal(cancellationToken);
        await _motion.MoveToXYAsync(
            _settings.PickupPosition.X,
            _settings.PickupPosition.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
        _log?.LogInformation("Bolt timing {Bolt}: pickup feeder XY arrived, X={X}, Y={Y}, elapsed={ElapsedMs:F1} ms.",
            boltId, _settings.PickupPosition.X, _settings.PickupPosition.Y, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    internal async Task MoveToPickupZAsync(CancellationToken cancellationToken = default)
    {
        var boltId = ActiveBolt?.Id;
        var started = Stopwatch.GetTimestamp();
        EnsureCanMoveHorizontal(cancellationToken);
        await MoveZAsync(_settings.PickupPosition.Z, cancellationToken);
        _log?.LogInformation("Bolt timing {Bolt}: pickup descent Z={Z} arrived, elapsed={ElapsedMs:F1} ms.",
            boltId, _settings.PickupPosition.Z, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private void EnsureCanMoveHorizontal(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsHorizontalMoveAllowed)
        {
            throw new MotionInterlockException("Raise both fastening heads before moving X/Y.");
        }
    }
}
