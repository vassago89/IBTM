using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFeeder;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.BoltFastening;

public sealed partial class BoltFasteningStation : AutoUnit
{
    private readonly IBoltHead _shootingHead;
    private readonly IBoltHead _pickupHead;
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly BoltFasteningSettings _settings;
    private readonly CarrierReferenceSettings _carrierReference;
    private readonly BoltFasteningWork _work;
    private readonly BoltFeederUnit _pickupFeeder;
    private readonly BoltFeederUnit _shootingFeeder;
    private readonly RecipeManager _recipes;
    private readonly UnitSettings _units;
    private HeatSinkSlot[]? _runTargets;
    // The result belongs to this bolt until collection or removal of its carrier.
    private PendingFastening? _pendingFastening;
    // Command history when pickup confirmation is disabled, not a loaded-bolt state.
    private PickupAttempt? _pickupAttempt;

    public BoltFasteningStation(
        IBoltHead shootingHead,
        IBoltHead pickupHead,
        IIoService io,
        IXyMotion motion,
        BoltFasteningSettings settings,
        CarrierReferenceSettings carrierReference,
        BoltFasteningWork work,
        BoltFeederUnit pickupFeeder,
        BoltFeederUnit shootingFeeder,
        RecipeManager recipes,
        UnitSettings units)
    {
        _shootingHead = shootingHead;
        _pickupHead = pickupHead;
        _io = io;
        _motion = motion;
        _settings = settings;
        _carrierReference = carrierReference;
        _work = work;
        _pickupFeeder = pickupFeeder;
        _shootingFeeder = shootingFeeder;
        _recipes = recipes;
        _units = units;
        Motion = new(motion);
        io.InputChanged += OnInputChanged;
        work.Changed += NotifyChanged;
        pickupFeeder.Changed += NotifyChanged;
        shootingFeeder.Changed += NotifyChanged;
    }

    public override event Action? Changed;

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    public MotionStatus Motion { get; }

    public IMotionFeedback Feedback => _motion;

    public bool PickupBoltLoaded => _io.GetInput(InputIo.PickupHeadVacuumDetected);

    public bool ShootingBoltLoaded => _io.GetInput(InputIo.ShootingHeadVacuumDetected);

    internal bool ShootingTubeBoltDetected => _io.GetInput(InputIo.ShootingTubeBoltDetected);

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

    public bool IsAtSafeZ(bool live = true)
    {
        return Motion.IsSettled(live, MotionAxis.Z)
            && (live ? _motion.IsAtHorizontalZ : Motion.IsAtZ(_settings.SafeZ));
    }

    internal bool IsAt(BoltPoint bolt, bool live = true, bool atTravelZ = false)
    {
        var position = _settings.GetBoltPosition(bolt, _carrierReference);
        if (atTravelZ)
            position.Z = _settings.SafeZ;
        return IsAt(position, live);
    }

    internal bool HasPosition(BoltPoint bolt)
    {
        return _settings.HasBoltPosition(bolt, _carrierReference);
    }

    public bool HasReference(FasteningHead head)
    {
        var reference = _settings.GetHead(head);
        return CarrierCoordinates.IsDefined(
            reference.UpperLeftLocatingPin,
            reference.LowerRightLocatingPin);
    }

    internal bool IsAtPickupPosition(bool live = true)
    {
        return IsAt(_settings.PickupPosition, live);
    }

    internal bool IsAtPickupXY(bool live = true)
    {
        var target = _settings.PickupPosition;
        var current = Motion.ReadPosition(live);
        return Motion.IsSettled(live, MotionAxis.X, MotionAxis.Y)
            && Math.Abs(current.X - target.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - target.Y) <= MotionService.PositionToleranceMillimeters;
    }

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
        await MoveToSafeZAsync(cancellationToken);
        EnsureCanMoveHorizontal(cancellationToken);
        await _motion.MoveToXYAsync(x, y, _settings.Motion.HorizontalSpeed, cancellationToken);
    }

    public Task MoveZAsync(double z, CancellationToken cancellationToken = default)
    {
        return _motion.MoveAxisAsync(MotionAxis.Z, z, _settings.Motion.ZSpeed, cancellationToken);
    }

    public async Task MoveToPickupPositionAsync(CancellationToken cancellationToken = default)
    {
        await MoveToPickupXYAsync(cancellationToken);
        await SetHeadDownAsync(FasteningHead.Pickup, true, cancellationToken);
        await MoveToPickupZAsync(cancellationToken);
    }

    public async Task ReturnFromPickupAsync(CancellationToken cancellationToken = default)
    {
        await MoveToSafeZAsync(cancellationToken);
        await SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken);
    }

    public Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default)
    {
        switch (point)
        {
            case { Target: TeachingTarget.BoltPickup }:
                return MoveToPickupPositionAsync(cancellationToken);
            case { Mode: TeachMode.XYOnly }:
                return MoveToXYAsync(position.X, position.Y, cancellationToken);
            case { Mode: TeachMode.ZOnly }:
                return MoveZAsync(position.Z, cancellationToken);
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

    public async Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _shootingHead.CheckReadyAsync(cancellationToken);
        await _pickupHead.CheckReadyAsync(cancellationToken);
    }

    public async Task ResetHeadsAsync(CancellationToken cancellationToken = default)
    {
        List<Exception>? failures = null;
        foreach (var head in new[] { _shootingHead, _pickupHead })
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

    internal async Task MoveToBoltAsync(
        BoltPoint bolt, CancellationToken cancellationToken = default, bool atTravelZ = false)
    {
        var position = _settings.GetBoltPosition(bolt, _carrierReference);
        // XY travel uses Safe Z. Approach the work height with both heads raised.
        await MoveToXYAsync(position.X, position.Y, cancellationToken);
        if (!atTravelZ)
        {
            EnsureCanMoveHorizontal(cancellationToken);
            await MoveZAsync(position.Z, cancellationToken);
        }
    }

    internal async Task FinishFasteningAsync(
        FasteningHead head,
        CancellationToken cancellationToken = default)
    {
        await SetVacuumAsync(head, false, cancellationToken);

        await SetHeadDownAsync(head, false, cancellationToken);
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

    internal Task SetPickupTableDownAsync(bool down, CancellationToken cancellationToken)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.PickupTableDown, down, cancellationToken);
    }

    public Task RaiseCylindersAsync(CancellationToken cancellationToken = default)
    {
        return Task.WhenAll(
            SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken),
            SetHeadDownAsync(FasteningHead.Shooting, false, cancellationToken));
    }

    internal async Task MoveToPickupXYAsync(CancellationToken cancellationToken = default)
    {
        await RaiseCylindersAsync(cancellationToken);
        await MoveToSafeZAsync(cancellationToken);
        if (PickupTablePosition != BoltCylinderState.Down)
            await SetPickupTableDownAsync(true, cancellationToken);
        await MoveToXYAsync(
            _settings.PickupPosition.X,
            _settings.PickupPosition.Y,
            cancellationToken);
    }

    internal Task MoveToPickupZAsync(CancellationToken cancellationToken = default)
    {
        return MoveZAsync(_settings.PickupPosition.Z, cancellationToken);
    }

    internal Task SetShootingEscapeForwardAsync(bool forward, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, forward, cancellationToken);
    }

    internal async Task ShootBoltAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
        using var passage = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var boltPassed = _io.WaitForInputAsync(
            InputIo.ShootingTubeBoltDetected, true, _settings.ShootingDetectionTimeoutMilliseconds, passage.Token);
        Exception? failure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _io.SetOutput(OutputIo.ShootBolt, true);
            await boltPassed;
            await Task.Delay(TimeSpan.FromSeconds(_settings.ShootingArrivalDelaySeconds), cancellationToken);
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
                await boltPassed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    internal Task WaitForShootingTubeClearAsync(CancellationToken cancellationToken = default)
    {
        return _io.WaitForInputAsync(
            InputIo.ShootingTubeBoltDetected, false, _settings.ShootingDetectionTimeoutMilliseconds, cancellationToken);
    }

    internal async Task WaitForBoltSupplyAsync(FasteningHead head, CancellationToken cancellationToken)
    {
        var changed = new AsyncAutoResetEvent();

        bool WaitingForSupply()
        {
            switch (head)
            {
                case FasteningHead.Pickup:
                    return !_io.GetInput(InputIo.PickupFeederBoltDetected)
                        && !PickupBoltLoaded
                        && PickupHeadPosition == BoltCylinderState.Down;
                case FasteningHead.Shooting:
                    return !_io.GetInput(InputIo.ShootingFeederBoltDetected)
                        && !ShootingBoltLoaded
                        && !ShootingTubeBoltDetected
                        && ShootingHeadPosition == BoltCylinderState.Up
                        && ShootingEscape == BoltEscapeState.Backward;
                default:
                    throw new ArgumentOutOfRangeException(nameof(head));
            }
        }

        void OnSupplyInputChanged(InputIo input, bool value)
        {
            var relevant = head switch
            {
                FasteningHead.Pickup => input is InputIo.PickupFeederBoltDetected
                    or InputIo.PickupHeadVacuumDetected
                    or InputIo.PickupHeadUp
                    or InputIo.PickupHeadDown,
                FasteningHead.Shooting => input is InputIo.ShootingFeederBoltDetected
                    or InputIo.ShootingHeadVacuumDetected
                    or InputIo.ShootingTubeBoltDetected
                    or InputIo.ShootingHeadUp
                    or InputIo.ShootingHeadDown
                    or InputIo.ShootingEscapeForward
                    or InputIo.ShootingEscapeBackward,
                _ => false,
            };
            if (relevant)
                changed.Set();
        }

        // Listen only during this supply wait; late head feedback can mean the bolt
        // already arrived. The station rechecks live state before its next action.
        _io.InputChanged += OnSupplyInputChanged;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (WaitingForSupply())
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

    public async Task MoveToSafeZAsync(CancellationToken cancellationToken = default)
    {
        await _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    public void StopShooting(Exception? operationFailure = null)
    {
        try
        {
            _io.SetOutput(OutputIo.ShootBolt, false);
        }
        catch (Exception cleanupFailure) when (operationFailure is not null)
        {
            throw new AggregateException(operationFailure, cleanupFailure);
        }
    }

    public void StopIoStart(FasteningHead head)
    {
        if (GetHead(head) is IoBoltHead ioHead)
        {
            ioHead.Stop();
            return;
        }

        _io.SetOutput(
            head == FasteningHead.Pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart,
            false);
    }

    internal void DiscardPendingResults()
    {
        _shootingHead.DiscardPendingResult();
        _pickupHead.DiscardPendingResult();
    }

    internal IBoltHead GetHead(FasteningHead head)
    {
        switch (head)
        {
            case FasteningHead.Shooting:
                return _shootingHead;
            case FasteningHead.Pickup:
                return _pickupHead;
            default:
                throw new ArgumentOutOfRangeException(nameof(head));
        }
    }

    private bool IsAt(AxisPosition target, bool live = true)
    {
        var current = Motion.ReadPosition(live);
        return Motion.IsSettled(live, MotionAxis.X, MotionAxis.Y)
            && Motion.ReadAxisState(MotionAxis.Z, live).InPosition
            && Math.Abs(current.X - target.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - target.Y) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Z - target.Z) <= MotionService.PositionToleranceMillimeters;
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
        var input = head == FasteningHead.Pickup
            ? InputIo.PickupHeadVacuumDetected
            : InputIo.ShootingHeadVacuumDetected;
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(output, on);
        if (waitForFeedback)
            await _io.WaitForInputAsync(input, on, cancellationToken);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.PickupHeadVacuumDetected
            or InputIo.ShootingHeadVacuumDetected
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
            Changed?.Invoke();
        }
    }

    private void EnsureCanMoveHorizontal(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsHorizontalMoveAllowed)
        {
            throw new MotionInterlockException("Raise both fastening heads before moving X/Y.");
        }
    }

    public bool HasPendingResult => PendingResult is not null;

    public bool HasUncollectedResults => HasPendingResult
        || _pickupHead.HasPendingResult || _shootingHead.HasPendingResult;

    private PendingFastening? PendingResult
    {
        get
        {
            var pending = _pendingFastening;
            return pending is not null
                && _work.Station.CarrierPresent
                && ReferenceEquals(_work.CurrentJob, pending.Job)
                && _work.Assemblies.Contains(pending.Assembly)
                ? pending
                : null;
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

    private IEnumerable<HeatSinkSlot> Targets
    {
        get
        {
            return _runTargets is { } targets
                ? targets
                : Enum.GetValues<HeatSinkSlot>().Where(_work.Station.IsHeatSinkPresent);
        }
    }

    public void DiscardRemovedCarrierResults()
    {
        if (_work.Station.CarrierPresent)
            throw new InvalidOperationException("Results still belong to the current fastening carrier.");
        if (_pendingFastening is { } pending)
            TraceStep(BoltFasteningState.Waiting, target: $"removed carrier: {pending.Bolt}", workId: pending.Job.Id);
        _pendingFastening = null;
        _pickupAttempt = null;
        DiscardPendingResults();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        _repeat = repeat;
        Exception? failure = null;
        try
        {
            BeginRun();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!_work.Enabled)
                    {
                        var job = _work.CurrentJob;
                        if (_work.Station.CarrierSeated)
                            _work.Complete(job);
                        TraceStep(BoltFasteningState.Disabled, workId: job.Id,
                            waitingFor: _work.Completed ? "carrier transfer" : "carrier seated");
                        await WaitForChangeAsync(cancellationToken);
                        continue;
                    }
                    if (repeat)
                        await RepeatCarrierAsync(cancellationToken);
                    else
                        await RunCarrierAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                EndRun(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _repeat = false;
            _pickupAttempt = null;
            if (_work.Enabled)
                StopShooting(failure);
        }
    }

    private async Task RunCarrierAsync(CancellationToken cancellationToken)
    {
        if (_pendingFastening is not null
            && PendingResult is null)
        {
            _pendingFastening = null;
            DiscardPendingResults();
        }

        if (!_work.IsReadyToFasten)
        {
            var state = GetState();
            TraceStep(state, GetActiveBolt(state)?.ToString(), _work.CurrentJob.Id);
            await ExecuteAsync(state, cancellationToken);
            return;
        }

        using var carrierOperation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckCarrier()
        {
            if (!_work.Station.CarrierSeated)
                carrierOperation.Cancel();
        }

        _runTargets = Enum.GetValues<HeatSinkSlot>().Where(_work.Station.IsHeatSinkPresent).ToArray();
        _work.Changed += CheckCarrier;
        try
        {
            CheckCarrier();
            carrierOperation.Token.ThrowIfCancellationRequested();
            foreach (var heatSink in _runTargets)
            {
                if (!_recipes.Current.Pcb.GetBolts(heatSink).Any())
                    throw new InvalidOperationException(
                        $"{heatSink.GetDescription()} has no taught bolts. Complete bolt teaching before fastening.");
            }
            while (_work.IsReadyToFasten)
            {
                carrierOperation.Token.ThrowIfCancellationRequested();
                if (PendingResult is { } pending)
                {
                    var head = GetHead(pending.Bolt.Head);
                    var result = await head.ReadPendingResultAsync(carrierOperation.Token);
                    if (result is not null)
                    {
                        RecordResult(pending, result);
                        continue;
                    }
                }

                var state = GetState();
                TraceStep(state, GetActiveBolt(state)?.ToString(), _work.CurrentJob.Id);
                await ExecuteAsync(state, carrierOperation.Token);
            }
        }
        catch (OperationCanceledException) when (carrierOperation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _work.Changed -= CheckCarrier;
            _runTargets = null;
            if (_pendingFastening is { } pending
                && !GetHead(pending.Bolt.Head).HasPendingResult)
            {
                _pendingFastening = null;
            }
        }
    }

    private async Task ExecuteAsync(
        BoltFasteningState state,
        CancellationToken cancellationToken)
    {
        switch (state)
        {
            case BoltFasteningState.MovingToStandby:
                await RaiseCylindersAsync(cancellationToken);
                await MoveToBoltAsync(StandbyBolt!, cancellationToken, atTravelZ: true);
                await SetPickupTableDownAsync(false, cancellationToken);
                break;
            case BoltFasteningState.WaitingForShootingFeeder:
                await WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken);
                break;
            case BoltFasteningState.WaitingForPickupFeeder:
                await WaitForBoltSupplyAsync(FasteningHead.Pickup, cancellationToken);
                break;
            case BoltFasteningState.FasteningPcb:
            {
                var bolt = PendingResult?.Bolt ?? PendingPcbBolts.First();
                var feeding = !_repeat && _units.IsBoltFeederEnabled(FasteningHead.Shooting);
                if (PickupTablePosition != BoltCylinderState.Up)
                {
                    await RaiseCylindersAsync(cancellationToken);
                    await MoveToSafeZAsync(cancellationToken);
                    await SetPickupTableDownAsync(false, cancellationToken);
                }
                if (ShootingHeadPosition != BoltCylinderState.Up
                    && (!IsAt(bolt) || feeding && !ShootingBoltLoaded))
                    await ClearHeadAsync(FasteningHead.Shooting, cancellationToken);
                if (feeding && ShootingTubeBoltDetected)
                    await WaitForShootingTubeClearAsync(cancellationToken);
                if (!IsAt(bolt))
                {
                    await RaiseCylindersAsync(cancellationToken);
                    await MoveToBoltAsync(bolt, cancellationToken);
                }

                if (feeding && PendingResult is null && !ShootingBoltLoaded)
                {
                    if (ShootingEscape == BoltEscapeState.Backward)
                        await WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken);
                    // Late head feedback can complete feeding while waiting for the feeder.
                    if (ShootingTubeBoltDetected)
                        await WaitForShootingTubeClearAsync(cancellationToken);
                    if (!ShootingBoltLoaded)
                    {
                        if (ShootingHeadPosition != BoltCylinderState.Up)
                            await RaiseCylindersAsync(cancellationToken);
                        if (ShootingEscape != BoltEscapeState.Forward)
                            await SetShootingEscapeForwardAsync(true, cancellationToken);
                        await ShootBoltAsync(cancellationToken);
                    }
                }
                if (feeding)
                {
                    await WaitForShootingTubeClearAsync(cancellationToken);
                    if (ShootingEscape != BoltEscapeState.Backward)
                        await SetShootingEscapeForwardAsync(false, cancellationToken);
                }
                await FastenAsync(FasteningHead.Shooting, cancellationToken);
                await ClearHeadAsync(FasteningHead.Shooting, cancellationToken);
                break;
            }
            case BoltFasteningState.FasteningPickup:
            {
                var bolt = PendingResult?.Bolt ?? PendingPickupBolts.First();
                if (ShootingHeadPosition != BoltCylinderState.Up)
                    await ClearHeadAsync(FasteningHead.Shooting, cancellationToken);
                if (PickupTablePosition != BoltCylinderState.Down)
                {
                    await RaiseCylindersAsync(cancellationToken);
                    await MoveToSafeZAsync(cancellationToken);
                    await SetPickupTableDownAsync(true, cancellationToken);
                }

                var feeding = !_repeat && _units.IsBoltFeederEnabled(FasteningHead.Pickup);
                var pickupAttempted = _pickupAttempt is { } attempt
                    && ReferenceEquals(attempt.Job, _work.CurrentJob)
                    && attempt.Bolt == bolt;
                if (PendingResult is null && (feeding ? !PickupBoltLoaded : !pickupAttempted))
                {
                    await MoveToPickupXYAsync(cancellationToken);
                    await SetHeadDownAsync(FasteningHead.Pickup, true, cancellationToken);
                    await MoveToPickupZAsync(cancellationToken);
                    if (feeding)
                        await WaitForBoltSupplyAsync(FasteningHead.Pickup, cancellationToken);
                    if (!feeding || !PickupBoltLoaded)
                    {
                        var job = _work.CurrentJob;
                        await SetVacuumAsync(
                            FasteningHead.Pickup, true, cancellationToken, waitForFeedback: feeding);
                        cancellationToken.ThrowIfCancellationRequested();
                        _work.RequireCurrentJob(job);
                        if (!feeding)
                            _pickupAttempt = new(job, bolt);
                    }
                }
                if (IsAtPickupXY())
                {
                    await MoveToSafeZAsync(cancellationToken);
                    await SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken);
                }
                if (!IsAt(bolt))
                {
                    await RaiseCylindersAsync(cancellationToken);
                    await MoveToBoltAsync(bolt, cancellationToken);
                }
                await FastenAsync(FasteningHead.Pickup, cancellationToken);
                await ClearHeadAsync(FasteningHead.Pickup, cancellationToken);
                break;
            }
            case BoltFasteningState.CompletingCarrier:
                var completedJob = _work.CurrentJob;
                foreach (var heatSink in Targets)
                    _work.GetAssembly(completedJob, heatSink).CompleteFastening();
                await FinishFasteningAsync(FasteningHead.Pickup, cancellationToken);
                await FinishFasteningAsync(FasteningHead.Shooting, cancellationToken);
                await MoveToSafeZAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _work.Complete(completedJob);
                break;
            default:
                await WaitForChangeAsync(cancellationToken);
                break;
        }
    }

    public BoltFasteningState GetState(bool live = true)
    {
        switch (true)
        {
            case true when !_work.Enabled:
                return BoltFasteningState.Disabled;
            case true when !_work.IsReadyToFasten:
                var standby = StandbyBolt;
                return standby is not null && HasPosition(standby)
                    && (!IsHorizontalMoveAllowed || !IsAt(standby, live, atTravelZ: true)
                        || PickupTablePosition != BoltCylinderState.Up)
                    ? BoltFasteningState.MovingToStandby
                    : BoltFasteningState.Waiting;
            case true when PendingResult is { } pending:
                return pending.Bolt.Head == FasteningHead.Shooting
                    ? BoltFasteningState.FasteningPcb
                    : BoltFasteningState.FasteningPickup;
            case true when PendingPcbBolts.FirstOrDefault() is { } shooting:
                return !_repeat && _units.IsBoltFeederEnabled(FasteningHead.Shooting)
                    && PickupTablePosition == BoltCylinderState.Up
                    && IsAt(shooting, live)
                    && ShootingHeadPosition == BoltCylinderState.Up
                    && !ShootingBoltLoaded && !ShootingTubeBoltDetected
                    && ShootingEscape == BoltEscapeState.Backward
                    && _shootingFeeder.State != BoltFeederState.BoltReady
                    ? BoltFasteningState.WaitingForShootingFeeder
                    : BoltFasteningState.FasteningPcb;
            case true when PendingPickupBolts.Any():
                return !_repeat && _units.IsBoltFeederEnabled(FasteningHead.Pickup)
                    && PickupTablePosition == BoltCylinderState.Down
                    && IsAtPickupPosition(live)
                    && PickupHeadPosition == BoltCylinderState.Down
                    && !PickupBoltLoaded && _pickupFeeder.State != BoltFeederState.BoltReady
                    ? BoltFasteningState.WaitingForPickupFeeder
                    : BoltFasteningState.FasteningPickup;
            default:
                return BoltFasteningState.CompletingCarrier;
        }
    }

    public BoltPoint? GetActiveBolt(BoltFasteningState? state = null)
    {
        if (PendingResult is { } pending)
            return pending.Bolt;
        switch (state ?? GetState())
        {
            case BoltFasteningState.MovingToStandby:
                return StandbyBolt;
            case BoltFasteningState.FasteningPcb or BoltFasteningState.WaitingForShootingFeeder:
                return PendingPcbBolts.FirstOrDefault();
            case BoltFasteningState.FasteningPickup or BoltFasteningState.WaitingForPickupFeeder:
                return PendingPickupBolts.FirstOrDefault();
            default:
                return null;
        }
    }

    private async Task FastenAsync(
        FasteningHead fasteningHead,
        CancellationToken cancellationToken)
    {
        var pending = PendingResult;
        if (pending is null)
        {
            var bolt = fasteningHead == FasteningHead.Shooting ? PendingPcbBolts.First() : PendingPickupBolts.First();
            var job = _work.CurrentJob;
            pending = new(bolt, job, _work.GetAssembly(job, bolt.HeatSink));
            await GetHead(bolt.Head).SelectPresetAsync(1, cancellationToken);
        }

        _pendingFastening = pending;
        var head = GetHead(pending.Bolt.Head);
        // The motor rotates only; the cylinder supplies the forward feed.
        // Raise before a new start, including a retry at the same XY.
        await RaiseCylindersAsync(cancellationToken);
        _work.RequireCurrentJob(pending.Job);
        if (!IsAt(pending.Bolt))
            throw new InvalidOperationException("The head must be at the bolt's fastening XYZ before starting.");

        using var fastening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPickupTable()
        {
            if (pending.Bolt.Head == FasteningHead.Shooting
                && PickupTablePosition != BoltCylinderState.Up)
                fastening.Cancel();
        }

        Changed += CheckPickupTable;
        try
        {
            CheckPickupTable();
            var completed = await head.TightenAsync(fastening.Token, LowerHeadWhileFasteningAsync);
            RecordResult(pending, completed);
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
            return SetHeadDownAsync(pending.Bolt.Head, true, token);
        }
    }

    private void RecordResult(PendingFastening pending, BoltResult result)
    {
        _work.RequireCurrentJob(pending.Job);
        switch (pending.Bolt.Head)
        {
            case FasteningHead.Shooting:
                pending.Assembly.RecordPcbBolt(pending.Bolt.Number, result);
                break;
            case FasteningHead.Pickup:
                pending.Assembly.RecordPickupBolt(pending.Bolt.Number, result);
                _pickupAttempt = null;
                break;
        }

        _pendingFastening = null;
    }

    private async Task ClearHeadAsync(FasteningHead head, CancellationToken cancellationToken)
    {
        await FinishFasteningAsync(head, cancellationToken);
        await RaiseCylindersAsync(cancellationToken);
        await MoveToSafeZAsync(cancellationToken);
    }

    private IEnumerable<BoltPoint> PendingPcbBolts
    {
        get
        {
            return ApplicableBolts
                .Where(bolt => bolt.Head == FasteningHead.Shooting)
                .Where(bolt => FindAssembly(bolt.HeatSink)?.PcbBoltResults.ContainsKey(bolt.Number) != true);
        }
    }

    private IEnumerable<BoltPoint> PendingPickupBolts
    {
        get
        {
            return ApplicableBolts
                .Where(bolt => bolt.Head == FasteningHead.Pickup)
                .Where(
                    bolt => FindAssembly(bolt.HeatSink)?.PickupBoltResults.ContainsKey(bolt.Number) != true);
        }
    }

    private HeatSinkAssembly? FindAssembly(HeatSinkSlot heatSink)
    {
        return _work.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == heatSink);
    }

    private IEnumerable<BoltPoint> ApplicableBolts
    {
        get
        {
            return _recipes.Current.Pcb
                .BoltPoints
                .Where(bolt => Targets.Contains(bolt.HeatSink))
                .OrderBy(bolt => bolt.HeatSink)
                .ThenBy(bolt => bolt.Number);
        }
    }

    private sealed record PendingFastening(
            BoltPoint Bolt, StationWork.Job Job, HeatSinkAssembly Assembly);

    private sealed record PickupAttempt(StationWork.Job Job, BoltPoint Bolt);
}
