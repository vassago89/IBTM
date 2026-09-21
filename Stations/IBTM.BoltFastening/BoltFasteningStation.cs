using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFeeder;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<BoltFasteningStation>? _log;
    private HeatSinkSlot[]? _runTargets;
    // Display the current loop destination only; never resume it after STOP.
    private BoltPoint? _activeBolt;

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
        UnitSettings units,
        ILogger<BoltFasteningStation>? log = null)
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
        _log = log;
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
        await MoveToPickupZAsync(cancellationToken);
    }

    public async Task ReturnFromPickupAsync(CancellationToken cancellationToken = default)
    {
        await MoveToSafeZAsync(cancellationToken);
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
                    throw new MotionInterlockException("Record the bolt and reference pins before moving to its fastening position.");
                var tableDown = bolt.Head == FasteningHead.Pickup;
                EnsureCanMoveHorizontal(cancellationToken);
                await MoveToSafeZAsync(cancellationToken);
                _log?.LogInformation("Bolt teaching Move To: Safe Z completed; requesting pickup table {Table}.",
                    tableDown ? "DOWN" : "UP");
                EnsureCanMoveHorizontal(cancellationToken);
                await SetPickupTableDownAsync(tableDown, cancellationToken);
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
                await MoveToPickupPositionAsync(cancellationToken);
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
        BoltPoint bolt, CancellationToken cancellationToken = default)
    {
        var position = _settings.GetBoltPosition(bolt, _carrierReference);
        _log?.LogInformation(
            "Automatic bolt move: {HeatSink}, bolt {Bolt}, {Head}; target X={X}, Y={Y}, Z={Z}.",
            bolt.HeatSink, bolt.Number, bolt.Head, position.X, position.Y, position.Z);
        // XY travel uses Safe Z. Approach the work height with both heads raised.
        await MoveToXYAsync(position.X, position.Y, cancellationToken);
        EnsureCanMoveHorizontal(cancellationToken);
        await MoveZAsync(position.Z, cancellationToken);
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
        EnsureCanMoveHorizontal(cancellationToken);
        return MoveZAsync(_settings.PickupPosition.Z, cancellationToken);
    }

    internal Task SetShootingEscapeForwardAsync(bool forward, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, forward, cancellationToken);
    }

    internal async Task ShootBoltAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ShootingEscape != BoltEscapeState.Backward)
            await SetShootingEscapeForwardAsync(false, cancellationToken);
        await WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken);
        await WaitForShootingTubeClearAsync(cancellationToken);
        await SetShootingEscapeForwardAsync(true, cancellationToken);
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
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(output, on);
        if (waitForFeedback && head == FasteningHead.Pickup)
            await _io.WaitForInputAsync(InputIo.PickupHeadVacuumDetected, on, cancellationToken);
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

    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _repeat = repeat;
        Exception? failure = null;
        try
        {
            BeginRun();
            if (_work.Enabled)
                _work.Restart(_work.CurrentJob);
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
            _activeBolt = null;
            if (_work.Enabled)
                StopShooting(failure);
        }
    }

    private async Task RunCarrierAsync(CancellationToken cancellationToken)
    {
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
            var job = _work.CurrentJob;
            foreach (var bolt in ApplicableBolts)
            {
                carrierOperation.Token.ThrowIfCancellationRequested();
                _work.RequireCurrentJob(job);
                _activeBolt = bolt;
                var state = bolt.Head == FasteningHead.Shooting
                    ? BoltFasteningState.FasteningPcb : BoltFasteningState.FasteningPickup;
                TraceStep(state, bolt.ToString(), job.Id);
                await ExecuteAsync(state, carrierOperation.Token);
            }
            carrierOperation.Token.ThrowIfCancellationRequested();
            _work.RequireCurrentJob(job);
            _activeBolt = null;
            TraceStep(BoltFasteningState.CompletingCarrier, workId: job.Id);
            await ExecuteAsync(BoltFasteningState.CompletingCarrier, carrierOperation.Token);
        }
        catch (OperationCanceledException) when (carrierOperation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _work.Changed -= CheckCarrier;
            _runTargets = null;
            _activeBolt = null;
        }
    }

    private async Task ExecuteAsync(
        BoltFasteningState state,
        CancellationToken cancellationToken)
    {
        switch (state)
        {
            case BoltFasteningState.MovingToStandby:
            {
                var position = _settings.GetBoltPosition(StandbyBolt!, _carrierReference);
                await RaiseCylindersAsync(cancellationToken);
                await MoveToSafeZAsync(cancellationToken);
                await SetPickupTableDownAsync(false, cancellationToken);
                EnsureCanMoveHorizontal(cancellationToken);
                await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, cancellationToken);
                break;
            }
            case BoltFasteningState.WaitingForShootingFeeder:
                await WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken);
                break;
            case BoltFasteningState.WaitingForPickupFeeder:
                await WaitForBoltSupplyAsync(FasteningHead.Pickup, cancellationToken);
                break;
            case BoltFasteningState.FasteningPcb:
            {
                var bolt = _activeBolt!;
                var feeding = !_repeat && _units.IsBoltFeederEnabled(FasteningHead.Shooting);
                if (PickupTablePosition != BoltCylinderState.Up)
                {
                    await RaiseCylindersAsync(cancellationToken);
                    await MoveToSafeZAsync(cancellationToken);
                    await SetPickupTableDownAsync(false, cancellationToken);
                }
                if (ShootingHeadPosition != BoltCylinderState.Up
                    && (!IsAt(bolt) || feeding))
                    await ClearHeadAsync(FasteningHead.Shooting, cancellationToken);
                var moveRequired = !IsAt(bolt);
                var shootRequired = feeding;
                if (moveRequired || shootRequired)
                    await RaiseCylindersAsync(cancellationToken);

                if (moveRequired && shootRequired)
                {
                    using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var moving = MoveToBoltAsync(bolt, preparation.Token);
                    if (moving.IsCompleted)
                        await moving;
                    var shooting = ShootBoltAsync(preparation.Token);
                    var first = await Task.WhenAny(moving, shooting);
                    if (!first.IsCompletedSuccessfully)
                        preparation.Cancel();
                    // Both operations must finish, including STOP/air-OFF cleanup on failure.
                    await Task.WhenAll(moving, shooting);
                }
                else if (moveRequired)
                    await MoveToBoltAsync(bolt, cancellationToken);
                else if (shootRequired)
                    await ShootBoltAsync(cancellationToken);
                if (feeding)
                {
                    await WaitForShootingTubeClearAsync(cancellationToken);
                    if (ShootingEscape != BoltEscapeState.Backward)
                        await SetShootingEscapeForwardAsync(false, cancellationToken);
                }
                await FastenAsync(bolt, cancellationToken);
                await ClearHeadAsync(FasteningHead.Shooting, cancellationToken);
                break;
            }
            case BoltFasteningState.FasteningPickup:
            {
                var bolt = _activeBolt!;
                if (ShootingHeadPosition != BoltCylinderState.Up)
                    await ClearHeadAsync(FasteningHead.Shooting, cancellationToken);
                if (PickupTablePosition != BoltCylinderState.Down)
                {
                    await RaiseCylindersAsync(cancellationToken);
                    await MoveToSafeZAsync(cancellationToken);
                    await SetPickupTableDownAsync(true, cancellationToken);
                }

                var feeding = !_repeat && _units.IsBoltFeederEnabled(FasteningHead.Pickup);
                if (!feeding || !PickupBoltLoaded)
                {
                    await MoveToPickupXYAsync(cancellationToken);
                    if (feeding)
                        await WaitForBoltSupplyAsync(FasteningHead.Pickup, cancellationToken);
                    await MoveToPickupZAsync(cancellationToken);
                    var job = _work.CurrentJob;
                    await SetVacuumAsync(
                        FasteningHead.Pickup, true, cancellationToken, waitForFeedback: feeding);
                    cancellationToken.ThrowIfCancellationRequested();
                    _work.RequireCurrentJob(job);
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
                await FastenAsync(bolt, cancellationToken);
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
        var bolt = _activeBolt ?? ApplicableBolts.FirstOrDefault();
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
            case true when bolt?.Head == FasteningHead.Shooting:
                return !_repeat && _units.IsBoltFeederEnabled(FasteningHead.Shooting)
                    && PickupTablePosition == BoltCylinderState.Up
                    && IsAt(bolt, live)
                    && ShootingHeadPosition == BoltCylinderState.Up
                    && !ShootingTubeBoltDetected
                    && ShootingEscape == BoltEscapeState.Backward
                    && _shootingFeeder.State != BoltFeederState.BoltReady
                    ? BoltFasteningState.WaitingForShootingFeeder
                    : BoltFasteningState.FasteningPcb;
            case true when bolt?.Head == FasteningHead.Pickup:
                return !_repeat && _units.IsBoltFeederEnabled(FasteningHead.Pickup)
                    && PickupTablePosition == BoltCylinderState.Down
                    && IsAtPickupXY(live) && IsAtSafeZ(live)
                    && PickupHeadPosition == BoltCylinderState.Up
                    && !PickupBoltLoaded && _pickupFeeder.State != BoltFeederState.BoltReady
                    ? BoltFasteningState.WaitingForPickupFeeder
                    : BoltFasteningState.FasteningPickup;
            default:
                return BoltFasteningState.CompletingCarrier;
        }
    }

    public BoltPoint? GetActiveBolt(BoltFasteningState? state = null)
    {
        switch (state ?? GetState())
        {
            case BoltFasteningState.MovingToStandby:
                return StandbyBolt;
            case BoltFasteningState.FasteningPcb or BoltFasteningState.WaitingForShootingFeeder
                or BoltFasteningState.FasteningPickup or BoltFasteningState.WaitingForPickupFeeder:
                return _activeBolt ?? ApplicableBolts.FirstOrDefault();
            default:
                return null;
        }
    }

    private async Task FastenAsync(
        BoltPoint bolt,
        CancellationToken cancellationToken)
    {
        var job = _work.CurrentJob;
        var assembly = _work.GetAssembly(job, bolt.HeatSink);
        var head = GetHead(bolt.Head);
        await head.SelectPresetAsync(1, cancellationToken);
        // The motor rotates only; the cylinder supplies the forward feed.
        // Raise before a new start, including a retry at the same XY.
        await RaiseCylindersAsync(cancellationToken);
        _work.RequireCurrentJob(job);
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
            _log?.LogInformation(
                "Bolt {Head}, {HeatSink}, point {Bolt}: starting {Controller}; requesting head DOWN and waiting for fastening result.",
                bolt.Head, bolt.HeatSink, bolt.Number, head.GetType().Name);
            var completed = await head.TightenAsync(fastening.Token, LowerHeadWhileFasteningAsync);
            _log?.LogInformation(
                "Bolt {Head}, {HeatSink}, point {Bolt}: result received; success={Success}, source={Source}, error={Error}.",
                bolt.Head, bolt.HeatSink, bolt.Number, completed.Success, completed.Source, completed.Error);
            _work.RequireCurrentJob(job);
            switch (bolt.Head)
            {
                case FasteningHead.Shooting:
                    assembly.RecordPcbBolt(bolt.Number, completed);
                    break;
                case FasteningHead.Pickup:
                    assembly.RecordPickupBolt(bolt.Number, completed);
                    break;
            }
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
            _log?.LogInformation("Bolt {Head}: head DOWN output sent; waiting for fastening result.", bolt.Head);
            return Task.CompletedTask;
        }
    }

    private async Task ClearHeadAsync(FasteningHead head, CancellationToken cancellationToken)
    {
        await FinishFasteningAsync(head, cancellationToken);
        await RaiseCylindersAsync(cancellationToken);
        await MoveToSafeZAsync(cancellationToken);
    }

    private IEnumerable<BoltPoint> ApplicableBolts
    {
        get
        {
            return _recipes.Current.Pcb
                .BoltPoints
                .Where(bolt => Targets.Contains(bolt.HeatSink))
                .OrderBy(bolt => bolt.Head == FasteningHead.Shooting ? 0 : 1)
                .ThenBy(bolt => bolt.HeatSink)
                .ThenBy(bolt => bolt.Number);
        }
    }

}
