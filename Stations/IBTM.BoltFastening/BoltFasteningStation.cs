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
    private readonly IIoService _io;
    private readonly BoltFeederUnit _feeder;
    private readonly IXyMotion _motion;
    private readonly BoltFasteningSettings _settings;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly RecipeManager _recipes;
    private readonly UnitSettings _units;
    private readonly ILogger<BoltFasteningStation>? _log;
    private HeatSinkSlot[]? _runTargets;
    // Selected work belongs only to this run; STOP discards it.
    private BoltPoint[]? _runBolts;
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
    {
        ShootingHead = shootingHead;
        PickupHead = pickupHead;
        _io = io;
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
        io.InputChanged += OnInputChanged;
        station.Changed += NotifyChanged;
    }

    public IBoltHead ShootingHead { get; }

    public IBoltHead PickupHead { get; }

    public ConveyorStation Station { get; }

    private bool IsReadyToFasten => Station.CarrierSeated && !Station.Completed;

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
            switch ((_io.GetInput(InputIo.PickupHeadUp), _io.GetInput(InputIo.PickupHeadDown)))
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
            switch ((_io.GetInput(InputIo.ShootingHeadUp), _io.GetInput(InputIo.ShootingHeadDown)))
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
            switch ((_io.GetInput(InputIo.PickupTableUp), _io.GetInput(InputIo.PickupTableDown)))
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

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.PickupHeadVacuumDetected
            or InputIo.ShootingTubeBoltDetected
            or InputIo.PickupHeadUp
            or InputIo.PickupHeadDown
            or InputIo.ShootingHeadUp
            or InputIo.ShootingHeadDown
            or InputIo.PickupTableUp
            or InputIo.PickupTableDown
            or InputIo.ShootingEscapeForward
            or InputIo.ShootingEscapeBackward)
        {
            NotifyChanged();
        }
    }

    public BoltPoint? ActiveBolt
    {
        get
        {
            var index = _boltIndex;
            return _runBolts is { } bolts && index < bolts.Length ? bolts[index] : null;
        }
    }

    private BoltPoint? StandbyBolt
    {
        get
        {
            return _recipes.Current.Pcb.FasteningPoints
                .FirstOrDefault(bolt => bolt.Head == FasteningHead.Shooting);
        }
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default,
        bool repeat = false,
        IReadOnlyCollection<Guid>? selectedBolts = null,
        Action<BoltPoint, BoltResult>? resultReceived = null)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        var testJob = selectedBolts is null ? null : Station.CurrentJob;
        if (selectedBolts is not null)
        {
            var bolts = _recipes.Current.Pcb.BoltPoints.Where(bolt => selectedBolts.Contains(bolt.Id)).ToArray();
            if (repeat || !_units.BoltFastening || selectedBolts.Count == 0
                || bolts.Length != selectedBolts.Distinct().Count())
                throw new InvalidOperationException("Select current recipe bolts for a single fastening test.");
            if (!Station.CarrierSeated || bolts.Any(bolt => !Station.IsHeatSinkPresent(bolt.HeatSink)))
                throw new InvalidOperationException("Seat the carrier and load every selected PCB before testing.");
            if (bolts.Any(bolt => !bolt.IsFasteningPositionDefined))
                throw new InvalidOperationException("Teach the fastening position of every selected bolt before testing.");
        }
        Exception? failure = null;
        try
        {
            BeginRun();
            if (_units.BoltFastening)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!repeat && _units.ShootingBoltFeeder
                    && (selectedBolts is null || _recipes.Current.Pcb.BoltPoints.Any(
                        bolt => bolt.Head == FasteningHead.Shooting && selectedBolts.Contains(bolt.Id))))
                    _io.SetOutput(OutputIo.ShootingEscapeForward, false);
                if (selectedBolts is not null)
                    Station.Restart(Station.CurrentJob);
                if (StandbyBolt is { IsFasteningPositionDefined: true } standby)
                {
                    var position = _settings.GetBoltPosition(standby);
                    EnterStep(BoltFasteningState.MovingToStandby,
                        $"Startup: Z=0 -> X={position.X}, Y={position.Y} -> Safe Z={_settings.SafeZ}",
                        Station.CurrentJob.Id);
                    var startupStarted = Stopwatch.GetTimestamp();
                    await RaiseCylindersAsync(cancellationToken);
                    await MoveZAsync(0, cancellationToken);
                    await _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, false, cancellationToken);
                    EnsureCanMoveHorizontal(cancellationToken);
                    await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, cancellationToken);
                    await MoveZAsync(_settings.SafeZ, cancellationToken);
                    _log?.LogInformation("Bolt timing {Bolt}: startup standby complete, total={ElapsedMs:F1} ms.",
                        standby.Id, Stopwatch.GetElapsedTime(startupStarted).TotalMilliseconds);
                }
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_units.BoltFastening && repeat && !_units.MainConveyor && Station.Completed)
                {
                    if (!Station.CarrierSeated || !IsHorizontalMoveAllowed || !IsAtSafeZ)
                        throw new InvalidOperationException("Fastening repeat requires the original seated carrier and both heads at safe height.");
                    Station.StartRepeat(Station.CurrentJob);
                }
                if (testJob is not null)
                {
                    Station.RequireCurrentJob(testJob);
                    if (!Station.CarrierSeated)
                        throw new MotionInterlockException("The test carrier is no longer seated.");
                    // A selected-bolt test never completes or transfers the whole carrier.
                    if (_runBolts is not null && _boltIndex == _runBolts.Length)
                    {
                        var started = Stopwatch.GetTimestamp();
                        await MoveZAsync(_settings.SafeZ, cancellationToken);
                        _log?.LogInformation("Bolt timing {Job}: selected test final Safe Z={Z} arrived, elapsed={ElapsedMs:F1} ms.",
                            testJob.Id, _settings.SafeZ, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        break;
                    }
                }
                var step = NextStep;
                if (!await ExecuteStepAsync(step, repeat, cancellationToken, selectedBolts, resultReceived))
                    await WaitForChangeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (MaintenanceStopException exception)
        {
            failure = exception;
            // Retries returned to Safe Z, or feed preparation joined its motion.
            await RaiseCylindersAsync(cancellationToken);
            await MoveZAsync(_settings.SafeZ, cancellationToken);
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
            {
                var standby = StandbyBolt;
                return standby is not null && standby.IsFasteningPositionDefined
                    && (!IsHorizontalMoveAllowed || !IsAt(standby, atSafeZ: true)
                        || PickupTablePosition != StationCylinderState.Up)
                    ? BoltFasteningState.MovingToStandby
                    : BoltFasteningState.Waiting;
            }

            if (_runJob is null)
                return BoltFasteningState.PreparingCarrier;

            var head = ActiveBolt?.Head;
            switch (head)
            {
                case null:
                    return BoltFasteningState.CompletingCarrier;
                case FasteningHead.Shooting:
                    return BoltFasteningState.FasteningShooting;
                case FasteningHead.Pickup:
                    return BoltFasteningState.FasteningPickup;
                default:
                    throw new ArgumentOutOfRangeException(nameof(BoltPoint.Head), head, "Unsupported fastening head.");
            }
        }
    }

    private async Task<bool> ExecuteStepAsync(
        BoltFasteningState step, bool repeat, CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? selectedBolts,
        Action<BoltPoint, BoltResult>? resultReceived)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selectedBolt = step == BoltFasteningState.MovingToStandby ? StandbyBolt : ActiveBolt;
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
                case BoltFasteningState.MovingToStandby:
                {
                    var standby = selectedBolt!;
                    var position = _settings.GetBoltPosition(standby);
                    var standbyStarted = Stopwatch.GetTimestamp();
                    await RaiseCylindersAsync(cancellationToken);
                    await MoveZAsync(_settings.SafeZ, cancellationToken);
                    await _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, false, cancellationToken);
                    EnsureCanMoveHorizontal(cancellationToken);
                    await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, cancellationToken);
                    _log?.LogInformation("Bolt timing {Bolt}: standby complete, total={ElapsedMs:F1} ms.",
                        standby.Id, Stopwatch.GetElapsedTime(standbyStarted).TotalMilliseconds);
                    return true;
                }
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
                    _runBolts = _recipes.Current.Pcb.FasteningPoints
                        .Where(bolt => _runTargets.Contains(bolt.HeatSink)
                            && (selectedBolts is null || selectedBolts.Contains(bolt.Id)))
                        .ToArray();
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
                    foreach (var heatSink in _runTargets!)
                        Station.GetAssembly(job, heatSink).CompleteFastening();
                    await FinishFasteningAsync(FasteningHead.Pickup, token);
                    await FinishFasteningAsync(FasteningHead.Shooting, token);
                    var started = Stopwatch.GetTimestamp();
                    await MoveZAsync(_settings.SafeZ, token);
                    _log?.LogInformation("Bolt timing {Job}: completion Safe Z={Z} arrived, elapsed={ElapsedMs:F1} ms.",
                        job.Id, _settings.SafeZ, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    token.ThrowIfCancellationRequested();
                    Station.Complete(job, Stopwatch.GetElapsedTime(_cycleStartedAt));
                    await ClearCarrierOperationAsync();
                    _log?.LogInformation("Bolt timing {Job}: carrier completion complete, total={ElapsedMs:F1} ms.",
                        job.Id, Stopwatch.GetElapsedTime(completionStarted).TotalMilliseconds);
                    return true;
                }

                case BoltFasteningState.FasteningShooting or BoltFasteningState.FasteningPickup:
                {
                    var token = operation?.Token ?? throw new InvalidOperationException("No fastening work is selected.");
                    token.ThrowIfCancellationRequested();
                    Station.RequireCurrentJob(job);
                    var bolt = selectedBolt ?? throw new InvalidOperationException("No bolt is selected.");
                    var cycleStarted = Stopwatch.GetTimestamp();
                    _log?.LogInformation("Bolt timing {Job}/{Bolt}: begin, PCB={Pcb}, head={Head}, safe Z={SafeZ}.",
                        job.Id, bolt.Id, bolt.HeatSink, bolt.Head, _settings.GetSafeZ(bolt.Head));
                    var feeding = !repeat && _units.IsBoltFeederEnabled(bolt.Head);
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
                                    await _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, false, token);
                                }
                                if (ShootingHeadPosition != StationCylinderState.Up
                                    && (!IsAt(bolt) || feeding))
                                    await ClearHeadAsync(FasteningHead.Shooting, _settings.GetSafeZ(FasteningHead.Shooting), token);
                            }
                            var moveRequired = !IsAt(bolt);
                            if (moveRequired || feeding)
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
                                var moving = moveRequired ? MoveToBoltAsync(bolt, preparation.Token) : Task.CompletedTask;
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
                            else if (moveRequired)
                                await MoveToBoltAsync(bolt, token);
                            break;
                        }
                        case FasteningHead.Pickup:
                        {
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
                                await _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, true, token);
                                _log?.LogInformation("Bolt timing {Bolt}: pickup table DOWN confirmed, elapsed={ElapsedMs:F1} ms.",
                                    bolt.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                            }

                            if (!_io.GetInput(InputIo.PickupHeadVacuumDetected))
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
                                        _io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
                                    }
                                    TraceStep(step, target, job.Id, $"pickup attempt {retry + 1}: return to Safe Z");
                                    await ReturnFromPickupAsync(token);
                                    token.ThrowIfCancellationRequested();
                                    if (!feeding)
                                        break;
                                    var vacuumDetected = _io.GetInput(InputIo.PickupHeadVacuumDetected);
                                    _log?.LogInformation("Bolt timing {Bolt}: pickup vacuum at Safe Z, attempt={Attempt}, detected={Detected}.",
                                        bolt.Id, retry + 1, vacuumDetected);
                                    if (vacuumDetected)
                                        break;
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
                            else if (IsAtPickupXY)
                                await ReturnFromPickupAsync(token);
                            if (!IsAt(bolt))
                            {
                                TraceStep(step, target, job.Id, "fastening point movement");
                                await RaiseCylindersAsync(token);
                                await MoveToBoltAsync(bolt, token);
                            }
                            break;
                        }
                        default:
                            throw new ArgumentOutOfRangeException(nameof(bolt.Head));
                    }
                    _log?.LogInformation("Bolt timing {Job}/{Bolt}: preparation ready, elapsed={ElapsedMs:F1} ms, feeding={Feeding}.",
                        job.Id, bolt.Id, Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds, feeding);
                    var assembly = Station.GetAssembly(job, bolt.HeatSink);
                    TraceStep(step, target, job.Id, "fastening controller result");
                    BoltResult? result = null;
                    Exception? fasteningFailure = null;
                    double? minimumTurns;
                    double? maximumTurns;
                    lock (_recipes.InspectionSync)
                    {
                        minimumTurns = bolt.MinimumTurns;
                        maximumTurns = bolt.MaximumTurns;
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
                        await head.SelectPresetAsync(1, token);
                        _log?.LogInformation("Bolt timing {Bolt}: preset selection, elapsed={ElapsedMs:F1} ms.",
                            bolt.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        started = Stopwatch.GetTimestamp();
                        // Raise before a new start, including a retry at the same XY.
                        await RaiseCylindersAsync(token);
                        _log?.LogInformation("Bolt timing {Bolt}: heads UP confirmed before START, elapsed={ElapsedMs:F1} ms.",
                            bolt.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        Station.RequireCurrentJob(job);
                        if (!IsAt(bolt))
                            throw new InvalidOperationException("The head must be at the bolt's fastening XYZ before starting.");

                        using (var fastening = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            void CheckPickupTable()
                            {
                                if (bolt.Head == FasteningHead.Shooting
                                    && PickupTablePosition != StationCylinderState.Up)
                                    OperationCancellation.CancelIfNotDisposed(fastening);
                            }

                            Task LowerHeadWhileFasteningAsync(CancellationToken feedToken)
                            {
                                feedToken.ThrowIfCancellationRequested();
                                _log?.LogInformation("Bolt {Head}: motor START completed; requesting head DOWN.", bolt.Head);
                                // Screw contact can stop the cylinder before its DOWN sensor.
                                _io.SetOutput(bolt.Head == FasteningHead.Pickup
                                    ? OutputIo.PickupHeadDown : OutputIo.ShootingHeadDown, true);
                                feedToken.ThrowIfCancellationRequested();
                                _log?.LogInformation("Bolt {Head}: head DOWN output sent.", bolt.Head);
                                return Task.CompletedTask;
                            }

                            Changed += CheckPickupTable;
                            try
                            {
                                CheckPickupTable();
                                var dryRunMilliseconds = repeat || !_units.IsBoltFeederEnabled(bolt.Head)
                                    ? _settings.DryRunMilliseconds : 0;
                                _log?.LogInformation(
                                    "Bolt {Head}, {HeatSink}, point {Bolt}: starting {Controller}; requesting head DOWN; dry run={DryRunMilliseconds} ms (0=wait for fastening result).",
                                    bolt.Head, bolt.HeatSink, bolt.Id, head.GetType().Name, dryRunMilliseconds);
                                started = Stopwatch.GetTimestamp();
                                var completed = await head.TightenAsync(
                                    fastening.Token, LowerHeadWhileFasteningAsync, dryRunMilliseconds, received => result = received);
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
                                throw new MotionInterlockException("Keep the pickup table raised during shooting fastening.");
                            }
                            finally
                            {
                                Changed -= CheckPickupTable;
                            }
                        }

                        // Finish physical clearance before publishing the measured result.
                        TraceStep(step, target, job.Id, "head retraction");
                        var nextBolt = _boltIndex + 1 < _runBolts!.Length ? _runBolts[_boltIndex + 1] : null;
                        var nextShootingBolt = bolt.Head == FasteningHead.Shooting && feeding
                            && nextBolt is { Head: FasteningHead.Shooting } ? nextBolt.Id : (Guid?)null;
                        var safeZ = bolt.Head == FasteningHead.Shooting && nextBolt is { Head: FasteningHead.Pickup }
                            ? _settings.SafeZ : _settings.GetSafeZ(bolt.Head);
                        await ClearHeadAsync(bolt.Head, safeZ, token, nextShootingBolt);
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
                                result = result with { MinimumTurns = minimumTurns, MaximumTurns = maximumTurns };
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
        if ((_io.GetInput(InputIo.ShootingEscapeForward), _io.GetInput(InputIo.ShootingEscapeBackward))
            is not (false, true))
            await _io.SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, false, cancellationToken);
        await WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken);
        await _io.WaitForInputAsync(
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
            await _io.SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, true, cancellationToken);
            _log?.LogInformation("Bolt timing {Bolt}: escape FORWARD, elapsed={ElapsedMs:F1} ms.",
                boltId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            started = Stopwatch.GetTimestamp();
            _io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
            boltPassed = _io.WaitForInputAsync(
                InputIo.ShootingTubeBoltDetected, true, _settings.ShootingDetectionTimeoutMilliseconds, passage.Token);
            cancellationToken.ThrowIfCancellationRequested();
            _io.SetOutput(OutputIo.ShootBolt, true);
            await boltPassed;
            _log?.LogInformation("Bolt timing {Bolt}: shot to tube ON, elapsed={ElapsedMs:F1} ms.",
                boltId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var arrivalStartedAt = Stopwatch.GetTimestamp();
            await _io.WaitForInputAsync(
                InputIo.ShootingTubeBoltDetected, false, _settings.ShootingDetectionTimeoutMilliseconds, cancellationToken);
            _log?.LogInformation("Bolt timing {Bolt}: tube passage OFF, elapsed={ElapsedMs:F1} ms.",
                boltId, Stopwatch.GetElapsedTime(arrivalStartedAt).TotalMilliseconds);
            started = Stopwatch.GetTimestamp();
            cancellationToken.ThrowIfCancellationRequested();
            _io.SetOutput(OutputIo.ShootingEscapeForward, false);
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

    public bool IsAtSafeZ
    {
        get
        {
            return MotionServiceBase.IsSettled(_motion, MotionAxis.Z)
                && MotionServiceBase.IsAtZ(_motion, _settings.SafeZ);
        }
    }

    internal bool IsAt(BoltPoint bolt, bool atSafeZ = false)
    {
        var position = _settings.GetBoltPosition(bolt);
        if (atSafeZ)
            position.Z = _settings.SafeZ;
        return MotionServiceBase.IsAt(_motion, position);
    }

    internal bool IsAtPickupXY
    {
        get
        {
            var target = _settings.PickupPosition;
            var current = _motion.Position;
            return MotionServiceBase.IsSettled(_motion, MotionAxis.X, MotionAxis.Y)
                && Math.Abs(current.X - target.X) <= MotionServiceBase.PositionToleranceMillimeters
                && Math.Abs(current.Y - target.Y) <= MotionServiceBase.PositionToleranceMillimeters;
        }
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
        return _io.SetOutputAndWaitAsync(output, down, cancellationToken);
    }

    public Task RaiseCylindersAsync(CancellationToken cancellationToken = default)
    {
        return Task.WhenAll(
            SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken),
            SetHeadDownAsync(FasteningHead.Shooting, false, cancellationToken));
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
        _io.InputChanged += OnBoltInputChanged;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (!_io.GetInput(boltDetected))
            {
                // Use a bolt still present, but never wait for a feeder that has stopped on an empty alarm.
                if (_feeder.EmptyAlarm is { } alarm)
                    throw new MaintenanceStopException("Bolt supply stopped. Remove the incomplete carrier before restarting.", alarm);
                await changed.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            _feeder.Changed -= changed.Set;
            _io.InputChanged -= OnBoltInputChanged;
        }
    }

    public void StopShooting(Exception? operationFailure = null)
    {
        Exception? cleanupFailure = null;
        try
        {
            _io.SetOutput(OutputIo.ShootBolt, false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }
        try
        {
            _io.SetOutput(OutputIo.ShootingEscapeForward, false);
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
        _io.SetOutput(
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
        _io.SetOutput(output, on);
        if (head == FasteningHead.Pickup)
            await _io.WaitForInputAsync(InputIo.PickupHeadVacuumDetected, on, cancellationToken, requireCurrent: true);
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
                    await _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, tableDown, cancellationToken);
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
            await _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, true, cancellationToken);
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
