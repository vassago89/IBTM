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
    private readonly Func<FasteningHead, bool>? _isFeederEnabled;
    private HeatSinkSlot[]? _runTargets;
    // The result belongs to this bolt/pass until collection or removal of its carrier.
    private PendingFastening? _pendingFastening;
    // Command history when pickup confirmation is disabled, not a loaded-bolt state.
    private PickupAttempt? _pickupAttempt;

    public BoltFasteningStation(
        BoltFasteningGantry gantry,
        BoltFasteningWork work,
        PickupBoltFeeder pickupFeeder,
        ShootingBoltFeeder shootingFeeder,
        RecipeManager recipes,
        Func<FasteningHead, bool>? isFeederEnabled = null)
    {
        _gantry = gantry;
        _work = work;
        _pickupFeeder = pickupFeeder;
        _shootingFeeder = shootingFeeder;
        _recipes = recipes;
        _isFeederEnabled = isFeederEnabled;
    }

    public override event Action? Changed
    {
        add
        {
            _work.Changed += value;
            _pickupFeeder.Changed += value;
            _shootingFeeder.Changed += value;
        }

        remove
        {
            _work.Changed -= value;
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
            TraceStep(BoltFasteningState.Waiting, target: $"removed carrier: {pending.Bolt}, {pending.Pass}", workId: pending.Job.Id);
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
            TraceStep(_work.State, workId: _work.CurrentJob.Id);
            await WaitForChangeAsync(cancellationToken);
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

    private Task ExecuteAsync(
        BoltFasteningRecipe recipe,
        BoltFasteningState state,
        CancellationToken cancellationToken)
    {
        switch (state)
        {
            case BoltFasteningState.FasteningPcb:
                return FastenAsync(recipe, FasteningPass.Pcb, cancellationToken);
            case BoltFasteningState.MovingToPcbBolt:
                return MoveToBoltAsync(
                    PendingResult?.Bolt ?? PendingPcbBolts.First(),
                    cancellationToken);
            case BoltFasteningState.WaitingForShootingFeeder:
                return _gantry.WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken);
            case BoltFasteningState.WaitingForPickupFeeder:
                return _gantry.WaitForBoltSupplyAsync(FasteningHead.Pickup, cancellationToken);
            case BoltFasteningState.ShootingBolt:
                return _gantry.ShootBoltAsync(cancellationToken);
            case BoltFasteningState.AdvancingShootingEscape:
                return _gantry.SetShootingEscapeForwardAsync(true, cancellationToken);
            case BoltFasteningState.WaitingForShootingTubeClear:
                return _gantry.WaitForShootingTubeClearAsync(cancellationToken);
            case BoltFasteningState.RetractingShootingEscape:
                return _gantry.SetShootingEscapeForwardAsync(false, cancellationToken);
            case BoltFasteningState.ClearingShootingHead:
                return ClearHeadAsync(FasteningHead.Shooting, cancellationToken);
            case BoltFasteningState.MovingToPickupXY:
                return _gantry.MoveToPickupXYAsync(cancellationToken);
            case BoltFasteningState.LoweringForBoltPickup:
                return _gantry.SetHeadDownAsync(FasteningHead.Pickup, true, cancellationToken);
            case BoltFasteningState.MovingToPickupZ:
                return _gantry.MoveToPickupZAsync(cancellationToken);
            case BoltFasteningState.PickingUpBolt:
                return PickUpBoltAsync(cancellationToken);
            case BoltFasteningState.RaisingPickedBolt:
                return _gantry.MoveToSafeZAsync(cancellationToken);
            case BoltFasteningState.RaisingPickupHead:
                return _gantry.SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken);
            case BoltFasteningState.MovingToIpmSeatingBolt:
                return MoveToBoltAsync(
                    PendingResult?.Bolt ?? PendingIpmSeatingBolts.First(),
                    cancellationToken);
            case BoltFasteningState.SeatingIpm:
                return FastenAsync(recipe, FasteningPass.IpmSeating, cancellationToken);
            case BoltFasteningState.MovingToIpmFinalBolt:
                return MoveToBoltAsync(
                    PendingResult?.Bolt ?? PendingIpmFinalBolts.First(),
                    cancellationToken);
            case BoltFasteningState.FinalizingIpm:
                return FastenAsync(recipe, FasteningPass.IpmFinal, cancellationToken);
            case BoltFasteningState.ClearingPickupHead:
                return ClearHeadAsync(FasteningHead.Pickup, cancellationToken);
            case BoltFasteningState.CompletingCarrier:
                return CompleteAsync(cancellationToken);
            default:
                return WaitForChangeAsync(cancellationToken);
        }
    }

    public BoltFasteningState GetState(bool live = true)
    {
        switch (true)
        {
            case true when _work.State != BoltFasteningWorkState.ReadyToFasten:
                return BoltFasteningState.Waiting;
            case true when PendingResult is { } pending:
                {
                    // A new START may retry the same bolt. Move from current feedback,
                    // without supplying another bolt or changing the result's owner.
                    var shootingFeederEnabled = pending.Pass == FasteningPass.Pcb
                        && IsFeederEnabled(FasteningHead.Shooting);
                    switch (true)
                    {
                        case true when shootingFeederEnabled && _gantry.ShootingTubeBoltDetected:
                            return BoltFasteningState.WaitingForShootingTubeClear;
                        case true when !_gantry.IsAt(pending.Bolt, live):
                            switch (pending.Pass)
                            {
                                case FasteningPass.Pcb:
                                    return BoltFasteningState.MovingToPcbBolt;
                                case FasteningPass.IpmSeating:
                                    return BoltFasteningState.MovingToIpmSeatingBolt;
                                case FasteningPass.IpmFinal:
                                    return BoltFasteningState.MovingToIpmFinalBolt;
                                default:
                                    throw new ArgumentOutOfRangeException(nameof(pending.Pass));
                            }
                        case true when shootingFeederEnabled && _gantry.ShootingEscape != BoltEscapeState.Backward:
                            return BoltFasteningState.RetractingShootingEscape;
                        default:
                            switch (pending.Pass)
                            {
                                case FasteningPass.Pcb:
                                    return BoltFasteningState.FasteningPcb;
                                case FasteningPass.IpmSeating:
                                    return BoltFasteningState.SeatingIpm;
                                case FasteningPass.IpmFinal:
                                    return BoltFasteningState.FinalizingIpm;
                                default:
                                    throw new ArgumentOutOfRangeException(nameof(pending.Pass));
                            }
                    }
                }
            case true when GetPcbState(PendingPcbBolts.FirstOrDefault(), live) is { } pcbState:
                return pcbState;
            case true when GetIpmSeatingState(PendingIpmSeatingBolts.FirstOrDefault(), live) is { } seatingState:
                return seatingState;
            default:
                return GetIpmFinalState(PendingIpmFinalBolts.FirstOrDefault(), live)
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
            case BoltFasteningState.MovingToPcbBolt:
            case BoltFasteningState.ShootingBolt:
            case BoltFasteningState.AdvancingShootingEscape:
            case BoltFasteningState.WaitingForShootingFeeder:
            case BoltFasteningState.WaitingForShootingTubeClear:
            case BoltFasteningState.RetractingShootingEscape:
            case BoltFasteningState.FasteningPcb:
                return PendingPcbBolts.FirstOrDefault();
            case BoltFasteningState.MovingToIpmSeatingBolt or BoltFasteningState.SeatingIpm:
                return PendingIpmSeatingBolts.FirstOrDefault();
            case BoltFasteningState.MovingToIpmFinalBolt or BoltFasteningState.FinalizingIpm:
                return PendingIpmFinalBolts.FirstOrDefault();
            default:
                return null;
        }
    }

    private BoltFasteningState? GetPcbState(BoltPoint? bolt, bool live = true)
    {
        var feeding = IsFeederEnabled(FasteningHead.Shooting);
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

    private BoltFasteningState? GetIpmSeatingState(BoltPoint? bolt, bool live = true)
    {
        if (bolt is null)
        {
            var finalBolt = PendingIpmFinalBolts.FirstOrDefault();
            return (!_gantry.IsAtSafeZ(live) || !_gantry.IsHorizontalMoveAllowed)
                && (finalBolt is null || !_gantry.IsAt(finalBolt, live))
                ? BoltFasteningState.ClearingPickupHead
                : null;
        }

        var feeding = IsFeederEnabled(FasteningHead.Pickup);
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
                : BoltFasteningState.MovingToIpmSeatingBolt;
        }

        return BoltFasteningState.SeatingIpm;
    }

    private BoltFasteningState? GetIpmFinalState(BoltPoint? bolt, bool live = true)
    {
        switch (true)
        {
            case true when bolt is null:
                return _gantry.IsAtSafeZ(live) && _gantry.IsHorizontalMoveAllowed
                    ? null
                    : BoltFasteningState.ClearingPickupHead;
            case true when !_gantry.IsAt(bolt, live):
                return !_gantry.IsHorizontalMoveAllowed
                    ? BoltFasteningState.ClearingPickupHead
                    : BoltFasteningState.MovingToIpmFinalBolt;
            default:
                return BoltFasteningState.FinalizingIpm;
        }
    }

    private async Task PickUpBoltAsync(CancellationToken cancellationToken)
    {
        var job = _work.CurrentJob;
        var bolt = PendingIpmSeatingBolts.First();
        var feeding = IsFeederEnabled(FasteningHead.Pickup);
        await _gantry.SetVacuumAsync(
            FasteningHead.Pickup, true, cancellationToken, waitForFeedback: feeding);
        cancellationToken.ThrowIfCancellationRequested();
        _work.RequireCurrentJob(job);
        if (!feeding)
            _pickupAttempt = new(job, bolt);
    }

    private async Task FastenAsync(
        BoltFasteningRecipe recipe,
        FasteningPass pass,
        CancellationToken cancellationToken)
    {
        var pending = PendingResult;
        if (pending is null)
        {
            var (bolt, preset) = pass switch
            {
                FasteningPass.Pcb => (PendingPcbBolts.First(), recipe.PcbPreset),
                FasteningPass.IpmSeating => (PendingIpmSeatingBolts.First(), recipe.IpmSeatingPreset),
                FasteningPass.IpmFinal => (PendingIpmFinalBolts.First(), recipe.IpmFinalPreset),
                _ => throw new ArgumentOutOfRangeException(nameof(pass)),
            };
            var job = _work.CurrentJob;
            pending = new(bolt, job, _work.GetAssembly(job, bolt.HeatSink), pass);
            await _gantry.GetHead(bolt.Head).SelectPresetAsync(preset, cancellationToken);
        }

        _pendingFastening = pending;
        var head = _gantry.GetHead(pending.Bolt.Head);
        // The motor rotates only; the cylinder supplies the forward feed.
        // Raise before a new start, including a retry or the next pass at the same XY.
        await _gantry.RaiseCylindersAsync(cancellationToken);
        _work.RequireCurrentJob(pending.Job);
        if (!_gantry.IsAt(pending.Bolt))
            throw new InvalidOperationException("The head must be at the bolt's fastening XYZ before starting.");

        var completed = await head.TightenAsync(cancellationToken, LowerHeadWhileFasteningAsync);
        RecordResult(pending, completed);

        Task LowerHeadWhileFasteningAsync(CancellationToken token)
        {
            return _gantry.SetHeadDownAsync(pending.Bolt.Head, true, token);
        }
    }

    private void RecordResult(PendingFastening pending, BoltResult result)
    {
        _work.RequireCurrentJob(pending.Job);
        switch (pending.Pass)
        {
            case FasteningPass.Pcb:
                pending.Assembly.RecordPcbBolt(pending.Bolt.Number, result);
                break;
            case FasteningPass.IpmSeating:
                pending.Assembly.RecordIpmSeating(pending.Bolt.Number, result);
                _pickupAttempt = null;
                break;
            case FasteningPass.IpmFinal:
                pending.Assembly.RecordIpmFinal(pending.Bolt.Number, result);
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

    private IEnumerable<BoltPoint> PendingIpmSeatingBolts
    {
        get
        {
            return ApplicableBolts
                .Where(bolt => bolt.Head == FasteningHead.Pickup)
                .Where(
                    bolt => FindAssembly(bolt.HeatSink)?.IpmSeatingResults.ContainsKey(bolt.Number) != true);
        }
    }

    private IEnumerable<BoltPoint> PendingIpmFinalBolts
    {
        get
        {
            return ApplicableBolts
                .Where(bolt => bolt.Head == FasteningHead.Pickup)
                .Where(bolt => FindAssembly(bolt.HeatSink)?.IpmFinalResults.ContainsKey(bolt.Number) != true);
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

    private bool IsFeederEnabled(FasteningHead head)
    {
        return _isFeederEnabled?.Invoke(head) ?? true;
    }

    private sealed record PendingFastening(
            BoltPoint Bolt, StationWork.Job Job, HeatSinkAssembly Assembly, FasteningPass Pass);

    private sealed record PickupAttempt(StationWork.Job Job, BoltPoint Bolt);
}
