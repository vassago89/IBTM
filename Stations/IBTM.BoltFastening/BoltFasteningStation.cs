using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFeeder;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningStation : AutoUnit
{
    private readonly BoltFasteningGantry _gantry;
    private readonly BoltFasteningWork _work;
    private readonly PickupBoltFeeder _pickupFeeder;
    private readonly ShootingBoltFeeder _shootingFeeder;
    private readonly Func<PcbLayout> _getPcb;
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
        Func<PcbLayout> getPcb,
        Func<FasteningHead, bool>? isFeederEnabled = null)
    {
        _gantry = gantry;
        _work = work;
        _pickupFeeder = pickupFeeder;
        _shootingFeeder = shootingFeeder;
        _getPcb = getPcb;
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

    public bool HasPendingResult
    {
        get
        {
            return PendingResult is not null;
        }
    }

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
            await RunLoopAsync(token => RunCarrierAsync(recipe, token), cancellationToken);
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
                if (!_getPcb().GetBolts(heatSink).Any())
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
        return state switch
        {
            BoltFasteningState.FasteningPcb => FastenAsync(recipe, FasteningPass.Pcb, cancellationToken),
            BoltFasteningState.MovingToPcbBolt
                => MoveToBoltAsync(
                    PendingResult?.Bolt ?? GetPendingPcbBolts().First(),
                    cancellationToken),
            BoltFasteningState.WaitingForShootingFeeder
                => _gantry.WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken),
            BoltFasteningState.WaitingForPickupFeeder
                => _gantry.WaitForBoltSupplyAsync(FasteningHead.Pickup, cancellationToken),
            BoltFasteningState.ShootingBolt => _gantry.ShootBoltAsync(cancellationToken),
            BoltFasteningState.AdvancingShootingEscape
                => _gantry.SetShootingEscapeForwardAsync(true, cancellationToken),
            BoltFasteningState.WaitingForShootingTubeClear
                => _gantry.WaitForShootingTubeClearAsync(cancellationToken),
            BoltFasteningState.RetractingShootingEscape
                => _gantry.SetShootingEscapeForwardAsync(false, cancellationToken),
            BoltFasteningState.ClearingShootingHead
                => ClearHeadAsync(FasteningHead.Shooting, cancellationToken),
            BoltFasteningState.MovingToPickupXY => _gantry.MoveToPickupXYAsync(cancellationToken),
            BoltFasteningState.LoweringForBoltPickup
                => _gantry.SetHeadDownAsync(FasteningHead.Pickup, true, cancellationToken),
            BoltFasteningState.MovingToPickupZ => _gantry.MoveToPickupZAsync(cancellationToken),
            BoltFasteningState.PickingUpBolt
                => PickUpBoltAsync(cancellationToken),
            BoltFasteningState.RaisingPickedBolt => _gantry.MoveToSafeZAsync(cancellationToken),
            BoltFasteningState.RaisingPickupHead
                => _gantry.SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken),
            BoltFasteningState.MovingToIpmSeatingBolt
                => MoveToBoltAsync(
                    PendingResult?.Bolt ?? GetPendingIpmSeatingBolts().First(),
                    cancellationToken),
            BoltFasteningState.SeatingIpm
                => FastenAsync(recipe, FasteningPass.IpmSeating, cancellationToken),
            BoltFasteningState.MovingToIpmFinalBolt
                => MoveToBoltAsync(
                    PendingResult?.Bolt ?? GetPendingIpmFinalBolts().First(),
                    cancellationToken),
            BoltFasteningState.FinalizingIpm
                => FastenAsync(recipe, FasteningPass.IpmFinal, cancellationToken),
            BoltFasteningState.ClearingPickupHead
                => ClearHeadAsync(FasteningHead.Pickup, cancellationToken),
            BoltFasteningState.CompletingCarrier => CompleteAsync(cancellationToken),
            _ => WaitForChangeAsync(cancellationToken),
        };
    }

    public BoltFasteningState GetState(bool live = true)
    {
        if (_work.State != BoltFasteningWorkState.ReadyToFasten)
        {
            return BoltFasteningState.Waiting;
        }

        if (PendingResult is { } pending)
        {
            // A new START may retry the same bolt. Move from current feedback,
            // without supplying another bolt or changing the result's owner.
            var shootingFeederEnabled = pending.Pass == FasteningPass.Pcb
                && IsFeederEnabled(FasteningHead.Shooting);
            if (shootingFeederEnabled && _gantry.ShootingTubeBoltDetected)
                return BoltFasteningState.WaitingForShootingTubeClear;
            if (!_gantry.IsAt(pending.Bolt, live))
            {
                return pending.Pass switch
                {
                    FasteningPass.Pcb => BoltFasteningState.MovingToPcbBolt,
                    FasteningPass.IpmSeating => BoltFasteningState.MovingToIpmSeatingBolt,
                    FasteningPass.IpmFinal => BoltFasteningState.MovingToIpmFinalBolt,
                    _ => throw new ArgumentOutOfRangeException(nameof(pending.Pass)),
                };
            }
            if (shootingFeederEnabled && _gantry.ShootingEscape != BoltEscapeState.Backward)
                return BoltFasteningState.RetractingShootingEscape;
            return pending.Pass switch
            {
                FasteningPass.Pcb => BoltFasteningState.FasteningPcb,
                FasteningPass.IpmSeating => BoltFasteningState.SeatingIpm,
                FasteningPass.IpmFinal => BoltFasteningState.FinalizingIpm,
                _ => throw new ArgumentOutOfRangeException(nameof(pending.Pass)),
            };
        }

        if (GetPcbState(GetPendingPcbBolts().FirstOrDefault(), live) is { } pcbState)
        {
            return pcbState;
        }

        if (GetIpmSeatingState(GetPendingIpmSeatingBolts().FirstOrDefault(), live) is { } seatingState)
        {
            return seatingState;
        }

        return GetIpmFinalState(GetPendingIpmFinalBolts().FirstOrDefault(), live)
            ?? BoltFasteningState.CompletingCarrier;
    }

    public BoltTarget? GetActiveBolt(BoltFasteningState? state = null)
    {
        if (PendingResult is { } pending)
        {
            return pending.Bolt;
        }

        return (state ?? GetState()) switch
        {
            BoltFasteningState.MovingToPcbBolt
                or BoltFasteningState.ShootingBolt
                or BoltFasteningState.AdvancingShootingEscape
                or BoltFasteningState.WaitingForShootingFeeder
                or BoltFasteningState.WaitingForShootingTubeClear
                or BoltFasteningState.RetractingShootingEscape
                or BoltFasteningState.FasteningPcb
                => GetPendingPcbBolts().FirstOrDefault(),
            BoltFasteningState.MovingToIpmSeatingBolt
                or BoltFasteningState.SeatingIpm
                => GetPendingIpmSeatingBolts().FirstOrDefault(),
            BoltFasteningState.MovingToIpmFinalBolt
                or BoltFasteningState.FinalizingIpm
                => GetPendingIpmFinalBolts().FirstOrDefault(),
            _ => null,
        };
    }

    private BoltFasteningState? GetPcbState(BoltTarget? bolt, bool live = true)
    {
        var feeding = IsFeederEnabled(FasteningHead.Shooting);
        if ((bolt is null || !_gantry.IsAt(bolt, live) || feeding && !_gantry.ShootingBoltLoaded)
            && _gantry.ShootingHeadPosition != BoltCylinderState.Up)
        {
            return BoltFasteningState.ClearingShootingHead;
        }

        if (bolt is null)
        {
            return null;
        }

        if (feeding && _gantry.ShootingTubeBoltDetected)
        {
            return BoltFasteningState.WaitingForShootingTubeClear;
        }

        if (!_gantry.IsAt(bolt, live))
        {
            return BoltFasteningState.MovingToPcbBolt;
        }

        if (!feeding)
            return BoltFasteningState.FasteningPcb;

        if (!_gantry.ShootingBoltLoaded)
        {
            return _gantry.ShootingEscape switch
            {
                BoltEscapeState.Forward => BoltFasteningState.ShootingBolt,
                BoltEscapeState.Backward when _shootingFeeder.State != BoltFeederState.BoltReady
                    => BoltFasteningState.WaitingForShootingFeeder,
                _ => BoltFasteningState.AdvancingShootingEscape,
            };
        }

        if (_gantry.ShootingEscape != BoltEscapeState.Backward)
        {
            return BoltFasteningState.RetractingShootingEscape;
        }

        return BoltFasteningState.FasteningPcb;
    }

    private BoltFasteningState? GetIpmSeatingState(BoltTarget? bolt, bool live = true)
    {
        if (bolt is null)
        {
            var finalBolt = GetPendingIpmFinalBolts().FirstOrDefault();
            return (!_gantry.IsAtSafeZ(live) || !_gantry.CanMoveHorizontal)
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
            if (!_gantry.IsAtPickupXY(live))
            {
                return !_gantry.CanMoveHorizontal
                    ? BoltFasteningState.ClearingPickupHead
                    : BoltFasteningState.MovingToPickupXY;
            }

            if (_gantry.PickupHeadPosition != BoltCylinderState.Down)
            {
                return !_gantry.IsAtSafeZ(live)
                    ? BoltFasteningState.MovingToPickupXY
                    : BoltFasteningState.LoweringForBoltPickup;
            }

            if (!_gantry.IsAtPickupPosition(live))
            {
                return BoltFasteningState.MovingToPickupZ;
            }

            return !feeding || _pickupFeeder.State == BoltFeederState.BoltReady
                ? BoltFasteningState.PickingUpBolt
                : BoltFasteningState.WaitingForPickupFeeder;
        }

        if (_gantry.IsAtPickupXY(live))
        {
            if (!_gantry.IsAtSafeZ(live))
                return BoltFasteningState.RaisingPickedBolt;
            if (!_gantry.CanMoveHorizontal)
                return BoltFasteningState.RaisingPickupHead;
        }

        if (!_gantry.IsAt(bolt, live))
        {
            return !_gantry.CanMoveHorizontal
                ? BoltFasteningState.ClearingPickupHead
                : BoltFasteningState.MovingToIpmSeatingBolt;
        }

        return BoltFasteningState.SeatingIpm;
    }

    private BoltFasteningState? GetIpmFinalState(BoltTarget? bolt, bool live = true)
    {
        if (bolt is null)
        {
            return _gantry.IsAtSafeZ(live) && _gantry.CanMoveHorizontal
                ? null
                : BoltFasteningState.ClearingPickupHead;
        }

        if (!_gantry.IsAt(bolt, live))
        {
            return !_gantry.CanMoveHorizontal
                ? BoltFasteningState.ClearingPickupHead
                : BoltFasteningState.MovingToIpmFinalBolt;
        }

        return BoltFasteningState.FinalizingIpm;
    }

    private async Task PickUpBoltAsync(CancellationToken cancellationToken)
    {
        var job = _work.CurrentJob;
        var bolt = GetPendingIpmSeatingBolts().First();
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
                FasteningPass.Pcb => (GetPendingPcbBolts().First(), recipe.PcbPreset),
                FasteningPass.IpmSeating => (GetPendingIpmSeatingBolts().First(), recipe.IpmSeatingPreset),
                FasteningPass.IpmFinal => (GetPendingIpmFinalBolts().First(), recipe.IpmFinalPreset),
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

    private async Task MoveToBoltAsync(BoltTarget bolt, CancellationToken cancellationToken)
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

    private IEnumerable<BoltTarget> GetPendingPcbBolts()
    {
        return GetApplicableBolts()
            .Where(bolt => bolt.Head == FasteningHead.Shooting)
            .Where(bolt => FindAssembly(bolt.HeatSink)?.PcbBoltResults.ContainsKey(bolt.Number) != true);
    }

    private IEnumerable<BoltTarget> GetPendingIpmSeatingBolts()
    {
        return GetApplicableBolts()
            .Where(bolt => bolt.Head == FasteningHead.Pickup)
            .Where(
                bolt => FindAssembly(bolt.HeatSink)?.IpmSeatingResults.ContainsKey(bolt.Number) != true);
    }

    private IEnumerable<BoltTarget> GetPendingIpmFinalBolts()
    {
        return GetApplicableBolts()
            .Where(bolt => bolt.Head == FasteningHead.Pickup)
            .Where(bolt => FindAssembly(bolt.HeatSink)?.IpmFinalResults.ContainsKey(bolt.Number) != true);
    }

    private HeatSinkAssembly? FindAssembly(HeatSinkSlot heatSink)
    {
        return _work.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == heatSink);
    }

    private IEnumerable<BoltTarget> GetApplicableBolts()
    {
        return _getPcb()
            .GetBolts()
            .Where(bolt => Targets.Contains(bolt.HeatSink))
            .OrderBy(bolt => bolt.HeatSink)
            .ThenBy(bolt => bolt.Number);
    }

    private bool IsFeederEnabled(FasteningHead head)
    {
        return _isFeederEnabled?.Invoke(head) ?? true;
    }

    private sealed record PendingFastening(
            BoltTarget Bolt, StationWork.Job Job, HeatSinkAssembly Assembly, FasteningPass Pass);

    private sealed record PickupAttempt(StationWork.Job Job, BoltTarget Bolt);
}
