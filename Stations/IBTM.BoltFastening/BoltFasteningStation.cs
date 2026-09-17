using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFeeder;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningStation(
    BoltFasteningGantry gantry,
    BoltFasteningWork work,
    PickupBoltFeeder pickupFeeder,
    ShootingBoltFeeder shootingFeeder,
    Func<PcbLayout> getPcb,
    Func<FasteningHead, bool>? isHeadEnabled = null) : AutoUnit
{
    private HeatSinkSlot[]? _runTargets;
    // The result belongs to this bolt/pass, even after STOP or a recovery edit.
    private PendingFastening? _pendingFastening;

    private sealed record PendingFastening(
        BoltTarget Bolt, StationWork.Job Job, HeatSinkAssembly Assembly, FasteningPass Pass);

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
                && work.CarrierPresent
                && ReferenceEquals(work.CurrentJob, pending.Job)
                && work.Assemblies.Contains(pending.Assembly)
                ? pending
                : null;
        }
    }

    public override event Action? Changed
    {
        add
        {
            work.Changed += value;
            gantry.Changed += value;
            pickupFeeder.Changed += value;
            shootingFeeder.Changed += value;
        }

        remove
        {
            work.Changed -= value;
            gantry.Changed -= value;
            pickupFeeder.Changed -= value;
            shootingFeeder.Changed -= value;
        }
    }

    public void PrepareRecovery(
        IEnumerable<(HeatSinkSlot HeatSink, int Number, FasteningPass Pass, bool Completed)> items)
    {
        var recovery = items.ToArray();
        work.PrepareRecovery(recovery);
        // An explicit recovery decision may retry an interrupted IO cycle;
        // retain completed results and ADC event ownership until they are collected.
        if (PendingResult is not { } pending
            || !recovery.Any(item => item.HeatSink == pending.Bolt.HeatSink
                && item.Number == pending.Bolt.Number
                && item.Pass == pending.Pass
                && !item.Completed)
            || gantry.GetHead(pending.Bolt.Head) is IoBoltHead { WasInterrupted: true })
        {
            _pendingFastening = null;
            gantry.DiscardPendingResults();
        }
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
            gantry.StopShooting(failure);
        }
    }

    private async Task RunCarrierAsync(BoltFasteningRecipe recipe, CancellationToken cancellationToken)
    {
        if (_pendingFastening is not null
            && PendingResult is null)
        {
            _pendingFastening = null;
            gantry.DiscardPendingResults();
        }

        if (work.State != BoltFasteningWorkState.ReadyToFasten)
        {
            TraceStep(work.State, workId: work.CurrentJob.Id);
            await WaitForChangeAsync(cancellationToken);
            return;
        }

        using var carrierOperation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckCarrier()
        {
            if (!work.CarrierSeated)
                carrierOperation.Cancel();
        }

        _runTargets = Enum.GetValues<HeatSinkSlot>().Where(work.HeatSinkPresent).ToArray();
        work.Changed += CheckCarrier;
        try
        {
            CheckCarrier();
            carrierOperation.Token.ThrowIfCancellationRequested();
            foreach (var heatSink in _runTargets)
            {
                if ((IsHeadEnabled(FasteningHead.Pickup) || IsHeadEnabled(FasteningHead.Shooting))
                    && !getPcb().GetBolts(heatSink).Any())
                    throw new InvalidOperationException(
                        $"{heatSink.GetDescription()} has no taught bolts. Complete bolt teaching before fastening.");
            }
            while (work.State == BoltFasteningWorkState.ReadyToFasten)
            {
                carrierOperation.Token.ThrowIfCancellationRequested();
                if (PendingResult is { } pending)
                {
                    var head = gantry.GetHead(pending.Bolt.Head);
                    var result = await head.ReadPendingResultAsync(carrierOperation.Token);
                    if (result is not null)
                    {
                        RecordResult(pending, result);
                        continue;
                    }
                    if (head is IoBoltHead { WasInterrupted: true })
                        throw new InvalidOperationException(
                            "Resolve the interrupted IO fastening in Recovery before moving the head or restarting.");
                    if (!IsHeadEnabled(pending.Bolt.Head))
                        throw new InvalidOperationException(
                            $"Resolve the pending {pending.Bolt.Head} fastening in Recovery before running with its feeder OFF.");
                }

                var state = State();
                TraceStep(state, ActiveBolt(state)?.ToString(), work.CurrentJob.Id);
                await ExecuteAsync(recipe, state, carrierOperation.Token);
            }
        }
        catch (OperationCanceledException) when (carrierOperation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            work.Changed -= CheckCarrier;
            _runTargets = null;
            if (_pendingFastening is { } pending
                && !gantry.GetHead(pending.Bolt.Head).HasPendingResult)
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
                    PendingResult?.Bolt ?? PendingPcbBolts().First(),
                    cancellationToken),
            BoltFasteningState.ShootingBolt => gantry.ShootBoltAsync(cancellationToken),
            BoltFasteningState.AdvancingShootingEscape
                => gantry.SetShootingEscapeForwardAsync(true, cancellationToken),
            BoltFasteningState.WaitingForShootingTubeClear
                => gantry.WaitForShootingTubeClearAsync(cancellationToken),
            BoltFasteningState.RetractingShootingEscape
                => gantry.SetShootingEscapeForwardAsync(false, cancellationToken),
            BoltFasteningState.ClearingShootingHead
                => ClearHeadAsync(FasteningHead.Shooting, cancellationToken),
            BoltFasteningState.MovingToPickupXY => gantry.MoveToPickupXYAsync(cancellationToken),
            BoltFasteningState.LoweringForBoltPickup
                => gantry.SetHeadDownAsync(FasteningHead.Pickup, true, cancellationToken),
            BoltFasteningState.MovingToPickupZ => gantry.MoveToPickupZAsync(cancellationToken),
            BoltFasteningState.PickingUpBolt
                => gantry.SetVacuumAsync(FasteningHead.Pickup, true, cancellationToken),
            BoltFasteningState.RaisingPickedBolt => gantry.MoveToSafeZAsync(cancellationToken),
            BoltFasteningState.RaisingPickupHead
                => gantry.SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken),
            BoltFasteningState.MovingToIpmSeatingBolt
                => MoveToBoltAsync(
                    PendingResult?.Bolt ?? PendingIpmSeatingBolts().First(),
                    cancellationToken),
            BoltFasteningState.SeatingIpm
                => FastenAsync(recipe, FasteningPass.IpmSeating, cancellationToken),
            BoltFasteningState.MovingToIpmFinalBolt
                => MoveToBoltAsync(
                    PendingResult?.Bolt ?? PendingIpmFinalBolts().First(),
                    cancellationToken),
            BoltFasteningState.FinalizingIpm
                => FastenAsync(recipe, FasteningPass.IpmFinal, cancellationToken),
            BoltFasteningState.ClearingPickupHead
                => ClearHeadAsync(FasteningHead.Pickup, cancellationToken),
            BoltFasteningState.CompletingCarrier => CompleteAsync(cancellationToken),
            _ => WaitForChangeAsync(cancellationToken),
        };
    }

    public BoltFasteningState State(bool live = true)
    {
        if (work.State != BoltFasteningWorkState.ReadyToFasten)
        {
            return BoltFasteningState.Waiting;
        }

        if (PendingResult is { } pending)
        {
            return (pending.Pass switch
            {
                FasteningPass.Pcb => PcbState(pending.Bolt, live),
                FasteningPass.IpmSeating => IpmSeatingState(pending.Bolt, live),
                FasteningPass.IpmFinal => IpmFinalState(pending.Bolt, live),
                _ => throw new ArgumentOutOfRangeException(nameof(pending.Pass)),
            }) ?? BoltFasteningState.Waiting;
        }

        if (PcbState(PendingPcbBolts().FirstOrDefault(), live) is { } pcbState)
        {
            return pcbState;
        }

        if (IpmSeatingState(PendingIpmSeatingBolts().FirstOrDefault(), live) is { } seatingState)
        {
            return seatingState;
        }

        return IpmFinalState(PendingIpmFinalBolts().FirstOrDefault(), live)
            ?? BoltFasteningState.CompletingCarrier;
    }

    public BoltTarget? ActiveBolt(BoltFasteningState? state = null)
    {
        if (PendingResult is { } pending)
        {
            return pending.Bolt;
        }

        return (state ?? State()) switch
        {
            BoltFasteningState.MovingToPcbBolt
                or BoltFasteningState.ShootingBolt
                or BoltFasteningState.AdvancingShootingEscape
                or BoltFasteningState.WaitingForShootingFeeder
                or BoltFasteningState.WaitingForShootingTubeClear
                or BoltFasteningState.RetractingShootingEscape
                or BoltFasteningState.FasteningPcb
                => PendingPcbBolts().FirstOrDefault(),
            BoltFasteningState.MovingToIpmSeatingBolt
                or BoltFasteningState.SeatingIpm
                => PendingIpmSeatingBolts().FirstOrDefault(),
            BoltFasteningState.MovingToIpmFinalBolt
                or BoltFasteningState.FinalizingIpm
                => PendingIpmFinalBolts().FirstOrDefault(),
            _ => null,
        };
    }

    private BoltFasteningState? PcbState(BoltTarget? bolt, bool live = true)
    {
        if ((bolt is null || !gantry.IsAt(bolt, live) || !gantry.ShootingBoltLoaded)
            && gantry.ShootingHeadPosition != BoltCylinderState.Up)
        {
            return BoltFasteningState.ClearingShootingHead;
        }

        if (bolt is null)
        {
            return null;
        }

        if (gantry.ShootingTubeBoltDetected)
        {
            return BoltFasteningState.WaitingForShootingTubeClear;
        }

        if (!gantry.IsAt(bolt, live))
        {
            return BoltFasteningState.MovingToPcbBolt;
        }

        if (!gantry.ShootingBoltLoaded)
        {
            return gantry.ShootingEscape switch
            {
                BoltEscapeState.Forward => BoltFasteningState.ShootingBolt,
                BoltEscapeState.Backward when shootingFeeder.State != BoltFeederState.BoltReady
                    => BoltFasteningState.WaitingForShootingFeeder,
                _ => BoltFasteningState.AdvancingShootingEscape,
            };
        }

        if (gantry.ShootingEscape != BoltEscapeState.Backward)
        {
            return BoltFasteningState.RetractingShootingEscape;
        }

        return BoltFasteningState.FasteningPcb;
    }

    private BoltFasteningState? IpmSeatingState(BoltTarget? bolt, bool live = true)
    {
        if (bolt is null)
        {
            var finalBolt = PendingIpmFinalBolts().FirstOrDefault();
            return (!gantry.IsAtSafeZ(live) || !gantry.CanMoveHorizontal)
                && (finalBolt is null || !gantry.IsAt(finalBolt, live))
                ? BoltFasteningState.ClearingPickupHead
                : null;
        }

        if (!gantry.PickupBoltLoaded)
        {
            if (!gantry.IsAtPickupXY(live))
            {
                return !gantry.CanMoveHorizontal
                    ? BoltFasteningState.ClearingPickupHead
                    : BoltFasteningState.MovingToPickupXY;
            }

            if (gantry.PickupHeadPosition != BoltCylinderState.Down)
            {
                return !gantry.IsAtSafeZ(live)
                    ? BoltFasteningState.MovingToPickupXY
                    : BoltFasteningState.LoweringForBoltPickup;
            }

            if (!gantry.IsAtPickupPosition(live))
            {
                return BoltFasteningState.MovingToPickupZ;
            }

            return pickupFeeder.State == BoltFeederState.BoltReady
                ? BoltFasteningState.PickingUpBolt
                : BoltFasteningState.WaitingForPickupFeeder;
        }

        if (gantry.IsAtPickupXY(live))
        {
            if (!gantry.IsAtSafeZ(live))
                return BoltFasteningState.RaisingPickedBolt;
            if (!gantry.CanMoveHorizontal)
                return BoltFasteningState.RaisingPickupHead;
        }

        if (!gantry.IsAt(bolt, live))
        {
            return !gantry.CanMoveHorizontal
                ? BoltFasteningState.ClearingPickupHead
                : BoltFasteningState.MovingToIpmSeatingBolt;
        }

        return BoltFasteningState.SeatingIpm;
    }

    private BoltFasteningState? IpmFinalState(BoltTarget? bolt, bool live = true)
    {
        if (bolt is null)
        {
            return gantry.IsAtSafeZ(live) && gantry.CanMoveHorizontal
                ? null
                : BoltFasteningState.ClearingPickupHead;
        }

        if (!gantry.IsAt(bolt, live))
        {
            return !gantry.CanMoveHorizontal
                ? BoltFasteningState.ClearingPickupHead
                : BoltFasteningState.MovingToIpmFinalBolt;
        }

        return BoltFasteningState.FinalizingIpm;
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
                FasteningPass.Pcb => (PendingPcbBolts().First(), recipe.PcbPreset),
                FasteningPass.IpmSeating => (PendingIpmSeatingBolts().First(), recipe.IpmSeatingPreset),
                FasteningPass.IpmFinal => (PendingIpmFinalBolts().First(), recipe.IpmFinalPreset),
                _ => throw new ArgumentOutOfRangeException(nameof(pass)),
            };
            var job = work.CurrentJob;
            pending = new(bolt, job, work.Assembly(job, bolt.HeatSink), pass);
            await gantry.GetHead(bolt.Head).SelectPresetAsync(preset, cancellationToken);
        }

        _pendingFastening = pending;
        var head = gantry.GetHead(pending.Bolt.Head);
        // The motor rotates only; the cylinder supplies the forward feed.
        // Raise before a new start, including a retry or the next pass at the same XY.
        await gantry.RaiseCylindersAsync(cancellationToken);
        work.RequireCurrentJob(pending.Job);
        if (!gantry.IsAt(pending.Bolt))
            throw new InvalidOperationException("The head must be at the bolt's fastening XYZ before starting.");
        var completed = await head.TightenAsync(cancellationToken, LowerHeadWhileFasteningAsync);
        RecordResult(pending, completed);

        Task LowerHeadWhileFasteningAsync(CancellationToken token)
        {
            return gantry.SetHeadDownAsync(pending.Bolt.Head, true, token);
        }
    }

    private void RecordResult(PendingFastening pending, BoltResult result)
    {
        work.RequireCurrentJob(pending.Job);
        switch (pending.Pass)
        {
            case FasteningPass.Pcb:
                pending.Assembly.RecordPcbBolt(pending.Bolt.Number, result);
                break;
            case FasteningPass.IpmSeating:
                pending.Assembly.RecordIpmSeating(pending.Bolt.Number, result);
                break;
            case FasteningPass.IpmFinal:
                pending.Assembly.RecordIpmFinal(pending.Bolt.Number, result);
                break;
        }

        _pendingFastening = null;
    }

    private async Task MoveToBoltAsync(BoltTarget bolt, CancellationToken cancellationToken)
    {
        await gantry.RaiseCylindersAsync(cancellationToken);
        await gantry.MoveToBoltAsync(bolt, cancellationToken);
    }

    private async Task ClearHeadAsync(FasteningHead head, CancellationToken cancellationToken)
    {
        await gantry.FinishFasteningAsync(head, cancellationToken);
        await gantry.RaiseCylindersAsync(cancellationToken);
        await gantry.MoveToSafeZAsync(cancellationToken);
    }

    private async Task CompleteAsync(CancellationToken cancellationToken)
    {
        var job = work.CurrentJob;
        foreach (var heatSink in Targets)
        {
            var assembly = work.Assembly(job, heatSink);
            // Excluded bolts have no result; do not report the whole assembly as fastened OK.
            if ((IsHeadEnabled(FasteningHead.Pickup) || IsHeadEnabled(FasteningHead.Shooting))
                && getPcb().GetBolts(heatSink).All(bolt => IsHeadEnabled(bolt.Head)))
                assembly.CompleteFastening();
        }

        await gantry.FinishFasteningAsync(FasteningHead.Pickup, cancellationToken);
        await gantry.FinishFasteningAsync(FasteningHead.Shooting, cancellationToken);
        await gantry.MoveToSafeZAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        work.Complete(job);
    }

    private IEnumerable<BoltTarget> PendingPcbBolts()
    {
        return ApplicableBolts()
            .Where(bolt => bolt.Head == FasteningHead.Shooting)
            .Where(bolt => FindAssembly(bolt.HeatSink)?.PcbBoltResults.ContainsKey(bolt.Number) != true);
    }

    private IEnumerable<BoltTarget> PendingIpmSeatingBolts()
    {
        return ApplicableBolts()
            .Where(bolt => bolt.Head == FasteningHead.Pickup)
            .Where(
                bolt => FindAssembly(bolt.HeatSink)?.IpmSeatingResults.ContainsKey(bolt.Number) != true);
    }

    private IEnumerable<BoltTarget> PendingIpmFinalBolts()
    {
        return ApplicableBolts()
            .Where(bolt => bolt.Head == FasteningHead.Pickup)
            .Where(bolt => FindAssembly(bolt.HeatSink)?.IpmFinalResults.ContainsKey(bolt.Number) != true);
    }

    private HeatSinkAssembly? FindAssembly(HeatSinkSlot heatSink)
    {
        return work.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == heatSink);
    }

    private IEnumerable<BoltTarget> ApplicableBolts()
    {
        return getPcb()
            .GetBolts()
            .Where(bolt => Targets.Contains(bolt.HeatSink))
            .Where(bolt => IsHeadEnabled(bolt.Head))
            .OrderBy(bolt => bolt.HeatSink)
            .ThenBy(bolt => bolt.Number);
    }

    private bool IsHeadEnabled(FasteningHead head)
    {
        return isHeadEnabled?.Invoke(head) ?? true;
    }

    private IEnumerable<HeatSinkSlot> Targets
    {
        get
        {
            return _runTargets is { } targets
                ? targets
                : Enum.GetValues<HeatSinkSlot>().Where(work.HeatSinkPresent);
        }
    }
}
