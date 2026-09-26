using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningStation : AutoUnit
{
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly BoltFasteningSettings _settings;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly RecipeManager _recipes;
    private readonly UnitSettings _units;
    private readonly ILogger<BoltFasteningStation>? _log;
    private bool _repeat;
    private HeatSinkSlot[]? _runTargets;
    // Selected work belongs only to this run; STOP discards it.
    private BoltPoint[]? _runBolts;
    private int _boltIndex;
    private ConveyorStation.Job? _runJob;
    private CancellationTokenSource? _carrierOperation;

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
        ILogger<BoltFasteningStation>? log = null)
    {
        ShootingHead = shootingHead;
        PickupHead = pickupHead;
        _io = io;
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

    public BoltCylinderState PickupHeadPosition
    {
        get
        {
            switch ((_io.GetInput(InputIo.PickupHeadUp), _io.GetInput(InputIo.PickupHeadDown)))
            {
                case (true, false):
                    return BoltCylinderState.Up;
                case (false, true):
                    return BoltCylinderState.Down;
                default:
                    return BoltCylinderState.Between;
            }
        }
    }

    public BoltCylinderState ShootingHeadPosition
    {
        get
        {
            switch ((_io.GetInput(InputIo.ShootingHeadUp), _io.GetInput(InputIo.ShootingHeadDown)))
            {
                case (true, false):
                    return BoltCylinderState.Up;
                case (false, true):
                    return BoltCylinderState.Down;
                default:
                    return BoltCylinderState.Between;
            }
        }
    }

    public BoltCylinderState PickupTablePosition
    {
        get
        {
            switch ((_io.GetInput(InputIo.PickupTableUp), _io.GetInput(InputIo.PickupTableDown)))
            {
                case (true, false):
                    return BoltCylinderState.Up;
                case (false, true):
                    return BoltCylinderState.Down;
                default:
                    return BoltCylinderState.Between;
            }
        }
    }

    public bool IsHorizontalMoveAllowed
    {
        get
        {
            return PickupHeadPosition == BoltCylinderState.Up
                && ShootingHeadPosition == BoltCylinderState.Up;
        }
    }

    internal BoltEscapeState ShootingEscape
    {
        get
        {
            switch ((
                _io.GetInput(InputIo.ShootingEscapeForward),
                _io.GetInput(InputIo.ShootingEscapeBackward)))
            {
                case (true, false):
                    return BoltEscapeState.Forward;
                case (false, true):
                    return BoltEscapeState.Backward;
                default:
                    return BoltEscapeState.Between;
            }
        }
    }

    internal IBoltHead GetHead(FasteningHead head)
    {
        switch (head)
        {
            case FasteningHead.Shooting:
                return ShootingHead;
            case FasteningHead.Pickup:
                return PickupHead;
            default:
                throw new ArgumentOutOfRangeException(nameof(head));
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
            return _recipes.Current.Pcb.GetBolts(HeatSinkSlot.HeatSink1)
                .Where(bolt => bolt.Head == FasteningHead.Shooting)
                .OrderBy(bolt => bolt.Number)
                .FirstOrDefault();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        _repeat = repeat;
        Exception? failure = null;
        try
        {
            BeginRun();
            if (_units.BoltFastening)
                Station.Restart(Station.CurrentJob);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_units.BoltFastening && repeat && !_units.MainConveyor && Station.Completed)
                {
                    if (!Station.CarrierSeated || !IsHorizontalMoveAllowed || !IsAtSafeZ)
                        throw new InvalidOperationException("Fastening repeat requires the original seated carrier and both heads at safe height.");
                    Station.StartRepeat(Station.CurrentJob);
                }
                var step = GetNextStep();
                if (!await ExecuteStepAsync(step, cancellationToken))
                    await WaitForChangeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _repeat = false;
            ClearCarrierOperation();
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

    internal BoltFasteningState GetNextStep()
    {
        if (!_units.BoltFastening)
            return BoltFasteningState.Disabled;
        if (!IsReadyToFasten)
        {
            var standby = StandbyBolt;
            return standby is not null && standby.IsFasteningPositionDefined
                && (!IsHorizontalMoveAllowed || !IsAt(standby, atSafeZ: true)
                    || PickupTablePosition != BoltCylinderState.Up)
                ? BoltFasteningState.MovingToStandby
                : BoltFasteningState.Waiting;
        }

        if (_runJob is null || _carrierOperation?.IsCancellationRequested == true
            || !ReferenceEquals(_runJob, Station.CurrentJob))
            return BoltFasteningState.PreparingCarrier;

        switch (ActiveBolt?.Head)
        {
            case FasteningHead.Shooting:
                return BoltFasteningState.FasteningPcb;
            case FasteningHead.Pickup:
                return BoltFasteningState.FasteningPickup;
            default:
                return BoltFasteningState.CompletingCarrier;
        }
    }

    private async Task<bool> ExecuteStepAsync(BoltFasteningState step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selectedBolt = step == BoltFasteningState.MovingToStandby ? StandbyBolt : ActiveBolt;
        var target = selectedBolt is null ? null
            : $"{selectedBolt.HeatSink}, bolt {selectedBolt.Number}, {selectedBolt.Head}";
        EnterStep(step, target, Station.CurrentJob.Id);
        switch (step)
        {
            case BoltFasteningState.Disabled:
                if (Station.CarrierSeated)
                    Station.Complete(Station.CurrentJob);
                return false;
            case BoltFasteningState.MovingToStandby:
                var position = _settings.GetBoltPosition(StandbyBolt!);
                await RaiseCylindersAsync(cancellationToken);
                await MoveZAsync(_settings.SafeZ, cancellationToken);
                await _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, false, cancellationToken);
                EnsureCanMoveHorizontal(cancellationToken);
                await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, cancellationToken);
                return true;
            case BoltFasteningState.Waiting:
                return false;
            case BoltFasteningState.PreparingCarrier:
                ClearCarrierOperation();
                _runTargets = Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent).ToArray();
                foreach (var heatSink in _runTargets)
                {
                    if (!_recipes.Current.Pcb.GetBolts(heatSink).Any())
                        throw new InvalidOperationException(
                            $"{heatSink.GetDescription()} has no taught bolts. Complete bolt teaching before fastening.");
                }
                _runJob = Station.CurrentJob;
                _runBolts = _recipes.Current.Pcb.BoltPoints
                    .Where(bolt => _runTargets.Contains(bolt.HeatSink))
                    .OrderBy(bolt => bolt.Head == FasteningHead.Shooting ? 0 : 1)
                    .ThenBy(bolt => bolt.HeatSink)
                    .ThenBy(bolt => bolt.Number)
                    .ToArray();
                _carrierOperation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Station.Changed += CheckCarrier;
                CheckCarrier();
                NotifyChanged();
                return true;
            case BoltFasteningState.FasteningPcb or BoltFasteningState.FasteningPickup or BoltFasteningState.CompletingCarrier:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }

        var operation = _carrierOperation
            ?? throw new InvalidOperationException("No fastening work is selected.");
        var job = _runJob!;
        var token = operation.Token;
        try
        {
            CheckCarrier();
            token.ThrowIfCancellationRequested();
            Station.RequireCurrentJob(job);
            if (step == BoltFasteningState.CompletingCarrier)
            {
                foreach (var heatSink in _runTargets!)
                    Station.GetAssembly(job, heatSink).CompleteFastening();
                await FinishFasteningAsync(FasteningHead.Pickup, token);
                await FinishFasteningAsync(FasteningHead.Shooting, token);
                await MoveZAsync(_settings.SafeZ, token);
                token.ThrowIfCancellationRequested();
                Station.Complete(job);
                ClearCarrierOperation();
                return true;
            }

            var bolt = ActiveBolt ?? throw new InvalidOperationException("No bolt is selected.");
            var feeding = !_repeat && _units.IsBoltFeederEnabled(bolt.Head);
            switch (bolt.Head)
            {
                case FasteningHead.Shooting:
                {
                    TraceStep(step, target, job.Id, "head/table clearance, shooting feed and point movement");
                    if (PickupTablePosition != BoltCylinderState.Up)
                    {
                        await RaiseCylindersAsync(token);
                        await MoveZAsync(_settings.SafeZ, token);
                        await _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, false, token);
                    }
                    if (ShootingHeadPosition != BoltCylinderState.Up
                        && (!IsAt(bolt) || feeding))
                        await ClearHeadAsync(FasteningHead.Shooting, token);
                    var moveRequired = !IsAt(bolt);
                    if (moveRequired || feeding)
                        await RaiseCylindersAsync(token);

                    if (moveRequired && feeding)
                    {
                        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var moving = MoveToBoltAsync(bolt, preparation.Token);
                        if (moving.IsCompleted)
                            await moving;
                        var shooting = ShootBoltAsync(preparation.Token);
                        var first = await Task.WhenAny(moving, shooting);
                        if (!first.IsCompletedSuccessfully)
                            preparation.Cancel();
                        // Drain both operations, including STOP/air-OFF cleanup on failure.
                        await Task.WhenAll(moving, shooting);
                    }
                    else if (moveRequired)
                        await MoveToBoltAsync(bolt, token);
                    else if (feeding)
                        await ShootBoltAsync(token);
                    break;
                }
                case FasteningHead.Pickup:
                {
                    if (ShootingHeadPosition != BoltCylinderState.Up)
                        await ClearHeadAsync(FasteningHead.Shooting, token);
                    if (PickupTablePosition != BoltCylinderState.Down)
                    {
                        await RaiseCylindersAsync(token);
                        await MoveZAsync(_settings.SafeZ, token);
                        await _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, true, token);
                    }

                    if (!_io.GetInput(InputIo.PickupHeadVacuumDetected))
                    {
                        TraceStep(step, target, job.Id, "pickup XY movement");
                        await MoveToPickupXYAsync(token);
                        var retryCount = _settings.PickupRetryCount;
                        for (var retry = 0; ; retry++)
                        {
                            token.ThrowIfCancellationRequested();
                            // Late feedback at Safe Z can confirm the previous attempt.
                            if (retry > 0 && _io.GetInput(InputIo.PickupHeadVacuumDetected))
                                break;
                            TraceStep(step, target, job.Id, $"pickup attempt {retry + 1}: bolt supply and descent");
                            if (feeding)
                                await WaitForBoltSupplyAsync(FasteningHead.Pickup, token);
                            await MoveToPickupZAsync(token);
                            if (feeding)
                                await SetVacuumAsync(FasteningHead.Pickup, true, token, waitForFeedback: false);
                            TraceStep(step, target, job.Id, $"pickup attempt {retry + 1}: return to Safe Z");
                            await ReturnFromPickupAsync(token);
                            if (!feeding)
                                break;
                            try
                            {
                                TraceStep(step, target, job.Id, $"pickup attempt {retry + 1}: vacuum detection at Safe Z");
                                await _io.WaitForInputAsync(InputIo.PickupHeadVacuumDetected, true, token);
                                break;
                            }
                            catch (IoTimeoutException) when (retry < retryCount && !token.IsCancellationRequested)
                            {
                                _log?.LogWarning(
                                    "Pickup bolt {Bolt}, {HeatSink}: vacuum not detected at Safe Z; retry {Retry}/{RetryCount}.",
                                    bolt.Number, bolt.HeatSink, retry + 1, retryCount);
                            }
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
            var assembly = Station.GetAssembly(job, bolt.HeatSink);
            TraceStep(step, target, job.Id, "fastening controller result");
            var result = await FastenAsync(bolt, token);
            Exception? clearFailure = null;
            try
            {
                // Result notifications can write to disk. Finish physical clearance first.
                TraceStep(step, target, job.Id, "head retraction");
                await ClearHeadAsync(bolt.Head, token);
            }
            catch (Exception exception)
            {
                clearFailure = exception;
                throw;
            }
            finally
            {
                // Keep the measured result with its original carrier even if clearance
                // is cancelled or fails. A storage failure must not hide a motion failure.
                try
                {
                    switch (bolt.Head)
                    {
                        case FasteningHead.Shooting:
                            assembly.RecordPcbBolt(bolt.Number, result);
                            break;
                        case FasteningHead.Pickup:
                            assembly.RecordPickupBolt(bolt.Number, result);
                            break;
                    }
                }
                catch (Exception recordFailure) when (clearFailure is not null)
                {
                    throw new AggregateException(clearFailure, recordFailure);
                }
            }

            token.ThrowIfCancellationRequested();
            Station.RequireCurrentJob(job);
            _boltIndex++;
            NotifyChanged();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            ClearCarrierOperation();
        }
        return true;
    }

    private void CheckCarrier()
    {
        if (_carrierOperation is not { } operation)
            return;
        lock (operation)
        {
            if (ReferenceEquals(operation, _carrierOperation)
                && (!Station.CarrierSeated || !ReferenceEquals(_runJob, Station.CurrentJob)))
                operation.Cancel();
        }
    }

    private void ClearCarrierOperation()
    {
        Station.Changed -= CheckCarrier;
        if (_carrierOperation is { } operation)
        {
            lock (operation)
            {
                _carrierOperation = null;
                operation.Dispose();
            }
        }
        _runJob = null;
        _runTargets = null;
        _runBolts = null;
        _boltIndex = 0;
    }

    internal async Task ShootBoltAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ShootingEscape != BoltEscapeState.Backward)
            await _io.SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, false, cancellationToken);
        await WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken);
        await _io.WaitForInputAsync(
            InputIo.ShootingTubeBoltDetected, false, _settings.ShootingDetectionTimeoutMilliseconds, cancellationToken);
        using var passage = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? boltPassed = null;
        Exception? failure = null;
        try
        {
            await _io.SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, true, cancellationToken);
            _io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
            boltPassed = _io.WaitForInputAsync(
                InputIo.ShootingTubeBoltDetected, true, _settings.ShootingDetectionTimeoutMilliseconds, passage.Token);
            cancellationToken.ThrowIfCancellationRequested();
            _io.SetOutput(OutputIo.ShootBolt, true);
            await boltPassed;
            var arrivalStartedAt = Stopwatch.GetTimestamp();
            await _io.WaitForInputAsync(
                InputIo.ShootingTubeBoltDetected, false, _settings.ShootingDetectionTimeoutMilliseconds, cancellationToken);
            await _io.SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, false, cancellationToken);
            var arrivalRemaining = TimeSpan.FromSeconds(_settings.ShootingArrivalDelaySeconds)
                - Stopwatch.GetElapsedTime(arrivalStartedAt);
            if (arrivalRemaining > TimeSpan.Zero)
                await Task.Delay(arrivalRemaining, cancellationToken);
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
        _log?.LogInformation("Bolt {Head}: requesting vacuum OFF.", head);
        await SetVacuumAsync(head, false, cancellationToken);
        _log?.LogInformation("Bolt {Head}: vacuum OFF request completed; requesting head UP.", head);
        await SetHeadDownAsync(head, false, cancellationToken);
        _log?.LogInformation("Bolt {Head}: head UP confirmed.", head);
    }

    private async Task<BoltResult> FastenAsync(
        BoltPoint bolt,
        CancellationToken cancellationToken)
    {
        var job = Station.CurrentJob;
        var head = GetHead(bolt.Head);
        await head.SelectPresetAsync(1, cancellationToken);
        // The motor rotates only; the cylinder supplies the forward feed.
        // Raise before a new start, including a retry at the same XY.
        await RaiseCylindersAsync(cancellationToken);
        Station.RequireCurrentJob(job);
        if (!IsAt(bolt))
            throw new InvalidOperationException("The head must be at the bolt's fastening XYZ before starting.");

        using var fastening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPickupTable()
        {
            if (bolt.Head == FasteningHead.Shooting
                && PickupTablePosition != BoltCylinderState.Up)
                fastening.Cancel();
        }

        Changed += CheckPickupTable;
        try
        {
            CheckPickupTable();
            var dryRunMilliseconds = _repeat || !_units.IsBoltFeederEnabled(bolt.Head)
                ? _settings.DryRunMilliseconds : 0;
            _log?.LogInformation(
                "Bolt {Head}, {HeatSink}, point {Bolt}: starting {Controller}; requesting head DOWN; dry run={DryRunMilliseconds} ms (0=wait for fastening result).",
                bolt.Head, bolt.HeatSink, bolt.Number, head.GetType().Name, dryRunMilliseconds);
            var completed = await head.TightenAsync(fastening.Token, LowerHeadWhileFasteningAsync, dryRunMilliseconds);
            completed = completed with { RecordedAt = completed.RecordedAt ?? DateTimeOffset.Now };
            _log?.LogInformation(
                "Bolt {Head}, {HeatSink}, point {Bolt}: cycle completed; success={Success}, source={Source}, error={Error}.",
                bolt.Head, bolt.HeatSink, bolt.Number, completed.Success, completed.Source, completed.Error);
            Station.RequireCurrentJob(job);
            return completed;
        }
        catch (OperationCanceledException) when (fastening.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new MotionInterlockException("Keep the pickup table raised during shooting fastening.");
        }
        finally
        {
            Changed -= CheckPickupTable;
        }

        Task LowerHeadWhileFasteningAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _log?.LogInformation("Bolt {Head}: motor START completed; requesting head DOWN.", bolt.Head);
            // Screw contact can stop the cylinder before its DOWN sensor.
            _io.SetOutput(bolt.Head == FasteningHead.Pickup
                ? OutputIo.PickupHeadDown : OutputIo.ShootingHeadDown, true);
            token.ThrowIfCancellationRequested();
            _log?.LogInformation("Bolt {Head}: head DOWN output sent.", bolt.Head);
            return Task.CompletedTask;
        }
    }

    private async Task ClearHeadAsync(FasteningHead head, CancellationToken cancellationToken)
    {
        await FinishFasteningAsync(head, cancellationToken);
        await RaiseCylindersAsync(cancellationToken);
        await MoveZAsync(_settings.SafeZ, cancellationToken);
    }

    public bool IsAtSafeZ
    {
        get
        {
            return MotionService.IsSettled(_motion, MotionAxis.Z)
                && MotionService.IsAtZ(_motion, _settings.SafeZ);
        }
    }

    internal bool IsAt(BoltPoint bolt, bool atSafeZ = false)
    {
        var position = _settings.GetBoltPosition(bolt);
        if (atSafeZ)
            position.Z = _settings.SafeZ;
        var current = _motion.Position;
        return MotionService.IsSettled(_motion, MotionAxis.X, MotionAxis.Y)
            && _motion.GetAxisState(MotionAxis.Z).InPosition
            && Math.Abs(current.X - position.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - position.Y) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Z - position.Z) <= MotionService.PositionToleranceMillimeters;
    }

    internal bool IsAtPickupXY
    {
        get
        {
            var target = _settings.PickupPosition;
            var current = _motion.Position;
            return MotionService.IsSettled(_motion, MotionAxis.X, MotionAxis.Y)
                && Math.Abs(current.X - target.X) <= MotionService.PositionToleranceMillimeters
                && Math.Abs(current.Y - target.Y) <= MotionService.PositionToleranceMillimeters;
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
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

        void OnSupplyInputChanged(InputIo input, bool value)
        {
            if (input == boltDetected)
                changed.Set();
        }

        // Subscribe before checking feedback so newly prepared supply is not missed.
        _io.InputChanged += OnSupplyInputChanged;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (!_io.GetInput(boltDetected))
            {
                await changed.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            _io.InputChanged -= OnSupplyInputChanged;
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
        CancellationToken cancellationToken,
        bool waitForFeedback = true)
    {
        var output = head == FasteningHead.Pickup
            ? OutputIo.PickupHeadVacuumPump
            : OutputIo.ShootingHeadVacuumPump;
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(output, on);
        if (waitForFeedback && head == FasteningHead.Pickup)
            await _io.WaitForInputAsync(InputIo.PickupHeadVacuumDetected, on, cancellationToken);
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
        await MoveZAsync(_settings.SafeZ, cancellationToken);
        await SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken);
    }

    public async Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default)
    {
        switch (point)
        {
            case { Target: TeachingTarget.BoltPosition, Bolt: { } bolt }:
            {
                _log?.LogInformation(
                    "Bolt teaching Move To: {HeatSink}, bolt {Bolt}, {Head}; target X={X}, Y={Y}, Z={Z}; Safe Z={SafeZ}.",
                    bolt.HeatSink, bolt.Number, bolt.Head, position.X, position.Y, position.Z, _settings.SafeZ);
                if (!point.HasPosition)
                    throw new MotionInterlockException("Record fastening XY before moving to this bolt.");
                var tableDown = bolt.Head == FasteningHead.Pickup;
                EnsureCanMoveHorizontal(cancellationToken);
                await MoveZAsync(_settings.SafeZ, cancellationToken);
                _log?.LogInformation("Bolt teaching Move To: Safe Z completed; requesting pickup table {Table}.",
                    tableDown ? "DOWN" : "UP");
                EnsureCanMoveHorizontal(cancellationToken);
                await _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, tableDown, cancellationToken);
                _log?.LogInformation("Bolt teaching Move To: pickup table feedback confirmed.");
                using var move = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                void CheckTeachingTable()
                {
                    if (PickupTablePosition != (tableDown ? BoltCylinderState.Down : BoltCylinderState.Up))
                        move.Cancel();
                }

                Changed += CheckTeachingTable;
                try
                {
                    CheckTeachingTable();
                    EnsureCanMoveHorizontal(move.Token);
                    _log?.LogInformation("Bolt teaching Move To: requesting XY, X={X}, Y={Y}.", position.X, position.Y);
                    await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, move.Token);
                    _log?.LogInformation("Bolt teaching Move To: XY command completed; requesting fastening Z={Z}.", position.Z);
                    CheckTeachingTable();
                    EnsureCanMoveHorizontal(move.Token);
                    await MoveZAsync(position.Z, move.Token);
                    move.Token.ThrowIfCancellationRequested();
                    _log?.LogInformation("Bolt teaching Move To: fastening Z completed.");
                }
                catch (OperationCanceledException) when (move.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new MotionInterlockException(
                        $"Keep the pickup table {(tableDown ? "down" : "up")} while moving to the selected bolt's fastening position.");
                }
                finally
                {
                    Changed -= CheckTeachingTable;
                }
                break;
            }
            case { Target: TeachingTarget.BoltPickup }:
                await MoveToPickupXYAsync(cancellationToken);
                await MoveToPickupZAsync(cancellationToken);
                break;
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
            bolt.HeatSink, bolt.Number, bolt.Head, position.X, position.Y, position.Z);
        // XY travel uses Safe Z. Approach the work height with both heads raised.
        await MoveToXYAsync(position.X, position.Y, cancellationToken);
        EnsureCanMoveHorizontal(cancellationToken);
        await MoveZAsync(position.Z, cancellationToken);
    }

    internal async Task MoveToPickupXYAsync(CancellationToken cancellationToken = default)
    {
        await RaiseCylindersAsync(cancellationToken);
        await MoveZAsync(_settings.SafeZ, cancellationToken);
        if (PickupTablePosition != BoltCylinderState.Down)
            await _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, true, cancellationToken);
        EnsureCanMoveHorizontal(cancellationToken);
        await _motion.MoveToXYAsync(
            _settings.PickupPosition.X,
            _settings.PickupPosition.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    internal Task MoveToPickupZAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanMoveHorizontal(cancellationToken);
        return MoveZAsync(_settings.PickupPosition.Z, cancellationToken);
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
