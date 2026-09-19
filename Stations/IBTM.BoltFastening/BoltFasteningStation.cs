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

public sealed class BoltFasteningStation : AutoUnit
{
    private readonly BoltFasteningGantry _gantry;
    private readonly BoltFasteningWork _work;
    private readonly PickupBoltFeeder _pickupFeeder;
    private readonly ShootingBoltFeeder _shootingFeeder;
    private readonly RecipeManager _recipes;
    private readonly UnitSettings _units;
    private HeatSinkSlot[]? _runTargets;
    // The result belongs to this bolt until collection or removal of its carrier.
    private PendingFastening? _pendingFastening;
    // Command history when pickup confirmation is disabled, not a loaded-bolt state.
    private PickupAttempt? _pickupAttempt;

    public BoltFasteningStation(
        BoltFasteningGantry gantry,
        BoltFasteningWork work,
        PickupBoltFeeder pickupFeeder,
        ShootingBoltFeeder shootingFeeder,
        RecipeManager recipes,
        UnitSettings units)
    {
        _gantry = gantry;
        _work = work;
        _pickupFeeder = pickupFeeder;
        _shootingFeeder = shootingFeeder;
        _recipes = recipes;
        _units = units;
    }

    public override event Action? Changed
    {
        add
        {
            _work.Changed += value;
            _gantry.Changed += value;
            _pickupFeeder.Changed += value;
            _shootingFeeder.Changed += value;
        }

        remove
        {
            _work.Changed -= value;
            _gantry.Changed -= value;
            _pickupFeeder.Changed -= value;
            _shootingFeeder.Changed -= value;
        }
    }

    public bool HasPendingResult => PendingResult is not null;

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
        _gantry.DiscardPendingResults();
    }

    public async Task RunAsync(BoltFasteningRecipe recipe, CancellationToken cancellationToken = default)
    {
        Exception? failure = null;
        try
        {
            BeginRun();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await RunCarrierAsync(recipe, cancellationToken);
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
            _pickupAttempt = null;
            _gantry.StopShooting(failure);
        }
    }

    private async Task RunCarrierAsync(BoltFasteningRecipe recipe, CancellationToken cancellationToken)
    {
        if (_pendingFastening is not null
            && PendingResult is null)
        {
            _pendingFastening = null;
            _gantry.DiscardPendingResults();
        }

        if (_work.State != BoltFasteningWorkState.ReadyToFasten)
        {
            var state = GetState();
            TraceStep(state, GetActiveBolt(state)?.ToString(), _work.CurrentJob.Id);
            await ExecuteAsync(recipe, state, cancellationToken);
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
            while (_work.State == BoltFasteningWorkState.ReadyToFasten)
            {
                carrierOperation.Token.ThrowIfCancellationRequested();
                if (PendingResult is { } pending)
                {
                    var head = _gantry.GetHead(pending.Bolt.Head);
                    var result = await head.ReadPendingResultAsync(carrierOperation.Token);
                    if (result is not null)
                    {
                        RecordResult(pending, result);
                        continue;
                    }
                }

                var state = GetState();
                TraceStep(state, GetActiveBolt(state)?.ToString(), _work.CurrentJob.Id);
                await ExecuteAsync(recipe, state, carrierOperation.Token);
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
                && !_gantry.GetHead(pending.Bolt.Head).HasPendingResult)
            {
                _pendingFastening = null;
            }
        }
    }

    private async Task ExecuteAsync(
        BoltFasteningRecipe recipe,
        BoltFasteningState state,
        CancellationToken cancellationToken)
    {
        switch (state)
        {
            case BoltFasteningState.MovingToStandby:
                await _gantry.RaiseCylindersAsync(cancellationToken);
                await _gantry.MoveToBoltAsync(StandbyBolt!, cancellationToken, atTravelZ: true);
                break;
            case BoltFasteningState.RaisingPickupTable or BoltFasteningState.LoweringPickupTable:
                await _gantry.RaiseCylindersAsync(cancellationToken);
                await _gantry.MoveToSafeZAsync(cancellationToken);
                await _gantry.SetPickupTableDownAsync(
                    state == BoltFasteningState.LoweringPickupTable, cancellationToken);
                break;
            case BoltFasteningState.FasteningPcb:
                await FastenAsync(recipe, FasteningHead.Shooting, cancellationToken);
                break;
            case BoltFasteningState.MovingToPcbBolt:
                await MoveToBoltAsync(PendingResult?.Bolt ?? PendingPcbBolts.First(), cancellationToken);
                break;
            case BoltFasteningState.WaitingForShootingFeeder:
                await _gantry.WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken);
                break;
            case BoltFasteningState.WaitingForPickupFeeder:
                await _gantry.WaitForBoltSupplyAsync(FasteningHead.Pickup, cancellationToken);
                break;
            case BoltFasteningState.ShootingBolt:
                await _gantry.ShootBoltAsync(cancellationToken);
                break;
            case BoltFasteningState.AdvancingShootingEscape:
                await _gantry.SetShootingEscapeForwardAsync(true, cancellationToken);
                break;
            case BoltFasteningState.WaitingForShootingTubeClear:
                await _gantry.WaitForShootingTubeClearAsync(cancellationToken);
                break;
            case BoltFasteningState.RetractingShootingEscape:
                await _gantry.SetShootingEscapeForwardAsync(false, cancellationToken);
                break;
            case BoltFasteningState.ClearingShootingHead:
                await ClearHeadAsync(FasteningHead.Shooting, cancellationToken);
                break;
            case BoltFasteningState.MovingToPickupXY:
                await _gantry.MoveToPickupXYAsync(cancellationToken);
                break;
            case BoltFasteningState.LoweringForBoltPickup:
                await _gantry.SetHeadDownAsync(FasteningHead.Pickup, true, cancellationToken);
                break;
            case BoltFasteningState.MovingToPickupZ:
                await _gantry.MoveToPickupZAsync(cancellationToken);
                break;
            case BoltFasteningState.PickingUpBolt:
                await PickUpBoltAsync(cancellationToken);
                break;
            case BoltFasteningState.RaisingPickedBolt:
                await _gantry.MoveToSafeZAsync(cancellationToken);
                break;
            case BoltFasteningState.RaisingPickupHead:
                await _gantry.SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken);
                break;
            case BoltFasteningState.MovingToPickupBolt:
                await MoveToBoltAsync(PendingResult?.Bolt ?? PendingPickupBolts.First(), cancellationToken);
                break;
            case BoltFasteningState.FasteningPickup:
                await FastenAsync(recipe, FasteningHead.Pickup, cancellationToken);
                break;
            case BoltFasteningState.ClearingPickupHead:
                await ClearHeadAsync(FasteningHead.Pickup, cancellationToken);
                break;
            case BoltFasteningState.CompletingCarrier:
                await CompleteAsync(cancellationToken);
                break;
            default:
                await WaitForChangeAsync(cancellationToken);
                break;
        }
    }

    public BoltFasteningState GetState(bool live = true)
    {
        var standby = StandbyBolt;
        switch (true)
        {
            case true when _work.State != BoltFasteningWorkState.ReadyToFasten:
                switch (true)
                {
                    case true when standby is null || !_gantry.HasPosition(standby):
                        return BoltFasteningState.Waiting;
                    case true when !_gantry.IsHorizontalMoveAllowed || !_gantry.IsAt(standby, live, atTravelZ: true):
                        return BoltFasteningState.MovingToStandby;
                    case true when _gantry.PickupTablePosition != BoltCylinderState.Up:
                        return BoltFasteningState.RaisingPickupTable;
                    default:
                        return BoltFasteningState.Waiting;
                }
            case true when PendingPcbBolts.Any() && _gantry.PickupTablePosition != BoltCylinderState.Up:
                return BoltFasteningState.RaisingPickupTable;
            case true when PendingResult is { } pending:
                {
                    // Keep an uncollected result with its carrier and bolt on a new START.
                    var shooting = pending.Bolt.Head == FasteningHead.Shooting;
                    var feeding = shooting && _units.IsBoltFeederEnabled(FasteningHead.Shooting);
                    switch (true)
                    {
                        case true when feeding && _gantry.ShootingTubeBoltDetected:
                            return BoltFasteningState.WaitingForShootingTubeClear;
                        case true when !_gantry.IsAt(pending.Bolt, live):
                            return shooting ? BoltFasteningState.MovingToPcbBolt : BoltFasteningState.MovingToPickupBolt;
                        case true when feeding && _gantry.ShootingEscape != BoltEscapeState.Backward:
                            return BoltFasteningState.RetractingShootingEscape;
                        default:
                            return shooting ? BoltFasteningState.FasteningPcb : BoltFasteningState.FasteningPickup;
                    }
                }
            case true when GetPcbState(PendingPcbBolts.FirstOrDefault(), live) is { } pcbState:
                return pcbState;
            default:
                return GetPickupState(PendingPickupBolts.FirstOrDefault(), live)
                    ?? BoltFasteningState.CompletingCarrier;
        }
    }

    public BoltPoint? GetActiveBolt(BoltFasteningState? state = null)
    {
        if (PendingResult is { } pending)
        {
            return pending.Bolt;
        }

        switch (state ?? GetState())
        {
            case BoltFasteningState.MovingToStandby:
                return StandbyBolt;
            case BoltFasteningState.MovingToPcbBolt:
            case BoltFasteningState.ShootingBolt:
            case BoltFasteningState.AdvancingShootingEscape:
            case BoltFasteningState.WaitingForShootingFeeder:
            case BoltFasteningState.WaitingForShootingTubeClear:
            case BoltFasteningState.RetractingShootingEscape:
            case BoltFasteningState.FasteningPcb:
                return PendingPcbBolts.FirstOrDefault();
            case BoltFasteningState.MovingToPickupBolt or BoltFasteningState.FasteningPickup:
                return PendingPickupBolts.FirstOrDefault();
            default:
                return null;
        }
    }

    private BoltFasteningState? GetPcbState(BoltPoint? bolt, bool live = true)
    {
        var feeding = _units.IsBoltFeederEnabled(FasteningHead.Shooting);
        switch (true)
        {
            case true when (bolt is null || !_gantry.IsAt(bolt, live) || feeding && !_gantry.ShootingBoltLoaded)
                && _gantry.ShootingHeadPosition != BoltCylinderState.Up:
                return BoltFasteningState.ClearingShootingHead;
            case true when bolt is null:
                return null;
            case true when feeding && _gantry.ShootingTubeBoltDetected:
                return BoltFasteningState.WaitingForShootingTubeClear;
            case true when !_gantry.IsAt(bolt, live):
                return BoltFasteningState.MovingToPcbBolt;
            case true when !feeding:
                return BoltFasteningState.FasteningPcb;
            case true when !_gantry.ShootingBoltLoaded:
                switch (_gantry.ShootingEscape)
                {
                    case BoltEscapeState.Forward:
                        return BoltFasteningState.ShootingBolt;
                    case BoltEscapeState.Backward when _shootingFeeder.State != BoltFeederState.BoltReady:
                        return BoltFasteningState.WaitingForShootingFeeder;
                    default:
                        return BoltFasteningState.AdvancingShootingEscape;
                }
            case true when _gantry.ShootingEscape != BoltEscapeState.Backward:
                return BoltFasteningState.RetractingShootingEscape;
            default:
                return BoltFasteningState.FasteningPcb;
        }
    }

    private BoltFasteningState? GetPickupState(BoltPoint? bolt, bool live = true)
    {
        if (bolt is null)
        {
            return !_gantry.IsAtSafeZ(live) || !_gantry.IsHorizontalMoveAllowed
                ? BoltFasteningState.ClearingPickupHead
                : null;
        }

        if (_gantry.PickupTablePosition != BoltCylinderState.Down)
            return BoltFasteningState.LoweringPickupTable;

        var feeding = _units.IsBoltFeederEnabled(FasteningHead.Pickup);
        var pickupAttempted = _pickupAttempt is { } attempt
            && ReferenceEquals(attempt.Job, _work.CurrentJob)
            && attempt.Bolt == bolt;
        if (feeding ? !_gantry.PickupBoltLoaded : !pickupAttempted)
        {
            switch (true)
            {
                case true when !_gantry.IsAtPickupXY(live):
                    return !_gantry.IsHorizontalMoveAllowed
                        ? BoltFasteningState.ClearingPickupHead
                        : BoltFasteningState.MovingToPickupXY;
                case true when _gantry.PickupHeadPosition != BoltCylinderState.Down:
                    return !_gantry.IsAtSafeZ(live)
                        ? BoltFasteningState.MovingToPickupXY
                        : BoltFasteningState.LoweringForBoltPickup;
                case true when !_gantry.IsAtPickupPosition(live):
                    return BoltFasteningState.MovingToPickupZ;
                default:
                    return !feeding || _pickupFeeder.State == BoltFeederState.BoltReady
                        ? BoltFasteningState.PickingUpBolt
                        : BoltFasteningState.WaitingForPickupFeeder;
            }
        }

        if (_gantry.IsAtPickupXY(live))
        {
            switch (true)
            {
                case true when !_gantry.IsAtSafeZ(live):
                    return BoltFasteningState.RaisingPickedBolt;
                case true when !_gantry.IsHorizontalMoveAllowed:
                    return BoltFasteningState.RaisingPickupHead;
            }
        }

        if (!_gantry.IsAt(bolt, live))
        {
            return !_gantry.IsHorizontalMoveAllowed
                ? BoltFasteningState.ClearingPickupHead
                : BoltFasteningState.MovingToPickupBolt;
        }

        return BoltFasteningState.FasteningPickup;
    }

    private async Task PickUpBoltAsync(CancellationToken cancellationToken)
    {
        var job = _work.CurrentJob;
        var bolt = PendingPickupBolts.First();
        var feeding = _units.IsBoltFeederEnabled(FasteningHead.Pickup);
        await _gantry.SetVacuumAsync(
            FasteningHead.Pickup, true, cancellationToken, waitForFeedback: feeding);
        cancellationToken.ThrowIfCancellationRequested();
        _work.RequireCurrentJob(job);
        if (!feeding)
            _pickupAttempt = new(job, bolt);
    }

    private async Task FastenAsync(
        BoltFasteningRecipe recipe,
        FasteningHead fasteningHead,
        CancellationToken cancellationToken)
    {
        var pending = PendingResult;
        if (pending is null)
        {
            var bolt = fasteningHead == FasteningHead.Shooting ? PendingPcbBolts.First() : PendingPickupBolts.First();
            var preset = fasteningHead == FasteningHead.Shooting ? recipe.PcbPreset : recipe.PickupPreset;
            var job = _work.CurrentJob;
            pending = new(bolt, job, _work.GetAssembly(job, bolt.HeatSink));
            await _gantry.GetHead(bolt.Head).SelectPresetAsync(preset, cancellationToken);
        }

        _pendingFastening = pending;
        var head = _gantry.GetHead(pending.Bolt.Head);
        // The motor rotates only; the cylinder supplies the forward feed.
        // Raise before a new start, including a retry at the same XY.
        await _gantry.RaiseCylindersAsync(cancellationToken);
        _work.RequireCurrentJob(pending.Job);
        if (!_gantry.IsAt(pending.Bolt))
            throw new InvalidOperationException("The head must be at the bolt's fastening XYZ before starting.");

        using var fastening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPickupTable()
        {
            if (pending.Bolt.Head == FasteningHead.Shooting
                && _gantry.PickupTablePosition != BoltCylinderState.Up)
                fastening.Cancel();
        }

        _gantry.Changed += CheckPickupTable;
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
            _gantry.Changed -= CheckPickupTable;
        }

        Task LowerHeadWhileFasteningAsync(CancellationToken token)
        {
            return _gantry.SetHeadDownAsync(pending.Bolt.Head, true, token);
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

    private async Task MoveToBoltAsync(BoltPoint bolt, CancellationToken cancellationToken)
    {
        await _gantry.RaiseCylindersAsync(cancellationToken);
        await _gantry.MoveToBoltAsync(bolt, cancellationToken);
    }

    private async Task ClearHeadAsync(FasteningHead head, CancellationToken cancellationToken)
    {
        await _gantry.FinishFasteningAsync(head, cancellationToken);
        await _gantry.RaiseCylindersAsync(cancellationToken);
        await _gantry.MoveToSafeZAsync(cancellationToken);
    }

    private async Task CompleteAsync(CancellationToken cancellationToken)
    {
        var job = _work.CurrentJob;
        foreach (var heatSink in Targets)
        {
            _work.GetAssembly(job, heatSink).CompleteFastening();
        }

        await _gantry.FinishFasteningAsync(FasteningHead.Pickup, cancellationToken);
        await _gantry.FinishFasteningAsync(FasteningHead.Shooting, cancellationToken);
        await _gantry.MoveToSafeZAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        _work.Complete(job);
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
