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
    Func<PcbLayout> getPcb) : AutoUnit
{
    private HeatSinkSlot[]? _runTargets;

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
        gantry.DiscardPendingResults();
        work.PrepareRecovery(items);
    }

    public async Task RunAsync(BoltFasteningRecipe recipe, CancellationToken cancellationToken = default)
    {
        try
        {
            await RunLoopAsync(token => RunCarrierAsync(recipe, token), cancellationToken);
        }
        finally
        {
            gantry.StopShooting();
        }
    }

    private async Task RunCarrierAsync(BoltFasteningRecipe recipe, CancellationToken cancellationToken)
    {
        if (work.State != BoltFasteningWorkState.ReadyToFasten)
        {
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
            while (work.State == BoltFasteningWorkState.ReadyToFasten)
            {
                carrierOperation.Token.ThrowIfCancellationRequested();
                await ExecuteAsync(recipe, State(), carrierOperation.Token);
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
            gantry.DiscardPendingResults();
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

                => MoveToBoltAsync(PendingPcbBolts().First(), cancellationToken),
            BoltFasteningState.LoweringForPcb => LowerShootingHeadAsync(cancellationToken),
            BoltFasteningState.ShootingBolt => gantry.ShootBoltAsync(cancellationToken),
            BoltFasteningState.AdvancingShootingEscape

                => gantry.AdvanceShootingEscapeAsync(cancellationToken),
            BoltFasteningState.WaitingForShootingTubeClear

                => gantry.WaitForShootingTubeClearAsync(cancellationToken),
            BoltFasteningState.RetractingShootingEscape

                => gantry.RetractShootingEscapeAsync(cancellationToken),
            BoltFasteningState.ClearingShootingHead

                => ClearHeadAsync(FasteningHead.Shooting, cancellationToken),
            BoltFasteningState.MovingToPickupXY => gantry.MoveToPickupXYAsync(cancellationToken),
            BoltFasteningState.LoweringForBoltPickup

                => gantry.SetPickupHeadDownAsync(true, cancellationToken),
            BoltFasteningState.MovingToPickupZ => gantry.MoveToPickupZAsync(cancellationToken),
            BoltFasteningState.PickingUpBolt => gantry.PickUpBoltAsync(cancellationToken),
            BoltFasteningState.RaisingPickedBolt => gantry.MoveToSafeZAsync(cancellationToken),
            BoltFasteningState.RaisingPickupHead

                => gantry.SetPickupHeadDownAsync(false, cancellationToken),
            BoltFasteningState.MovingToIpmSeatingBolt

                => MoveToBoltAsync(PendingIpmSeatingBolts().First(), cancellationToken),
            BoltFasteningState.LoweringForIpmSeating
                or BoltFasteningState.LoweringForIpmFinal

                => gantry.SetPickupHeadDownAsync(true, cancellationToken),
            BoltFasteningState.SeatingIpm

                => FastenAsync(recipe, FasteningPass.IpmSeating, cancellationToken),
            BoltFasteningState.MovingToIpmFinalBolt

                => MoveToBoltAsync(PendingIpmFinalBolts().First(), cancellationToken),
            BoltFasteningState.FinalizingIpm

                => FastenAsync(recipe, FasteningPass.IpmFinal, cancellationToken),
            BoltFasteningState.ClearingPickupHead

                => ClearHeadAsync(FasteningHead.Pickup, cancellationToken),
            BoltFasteningState.CompletingCarrier => CompleteAsync(cancellationToken),
            _ => WaitForChangeAsync(cancellationToken),
        };
    }

    public BoltFasteningState State()
    {
        if (work.State != BoltFasteningWorkState.ReadyToFasten)
        {
            return BoltFasteningState.Waiting;
        }

        if (PcbState() is { } pcbState)
        {
            return pcbState;
        }

        if (IpmSeatingState() is { } seatingState)
        {
            return seatingState;
        }

        return IpmFinalState() ?? BoltFasteningState.CompletingCarrier;
    }

    public BoltTarget? ActiveBolt()
    {
        return State() switch
        {
            BoltFasteningState.MovingToPcbBolt
                or BoltFasteningState.LoweringForPcb
                or BoltFasteningState.ShootingBolt
                or BoltFasteningState.AdvancingShootingEscape
                or BoltFasteningState.WaitingForShootingFeeder
                or BoltFasteningState.WaitingForShootingTubeClear
                or BoltFasteningState.RetractingShootingEscape
                or BoltFasteningState.FasteningPcb

                => PendingPcbBolts().FirstOrDefault(),
            BoltFasteningState.MovingToIpmSeatingBolt
                or BoltFasteningState.LoweringForIpmSeating
                or BoltFasteningState.SeatingIpm

                => PendingIpmSeatingBolts().FirstOrDefault(),
            BoltFasteningState.MovingToIpmFinalBolt
                or BoltFasteningState.LoweringForIpmFinal
                or BoltFasteningState.FinalizingIpm

                => PendingIpmFinalBolts().FirstOrDefault(),
            _ => null,
        };
    }

    private BoltFasteningState? PcbState()
    {
        var bolt = PendingPcbBolts().FirstOrDefault();
        if (gantry.HeadState(FasteningHead.Shooting) == BoltHeadState.Tightening)
        {
            return BoltFasteningState.FasteningPcb;
        }

        if ((bolt is null || !gantry.IsAt(bolt) || !gantry.ShootingBoltLoaded)
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

        if (!gantry.IsAt(bolt))
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

        return gantry.ShootingHeadPosition == BoltCylinderState.Down
            ? BoltFasteningState.FasteningPcb
            : BoltFasteningState.LoweringForPcb;
    }

    private BoltFasteningState? IpmSeatingState()
    {
        var bolt = PendingIpmSeatingBolts().FirstOrDefault();
        if (bolt is not null
            && gantry.HeadState(FasteningHead.Pickup) == BoltHeadState.Tightening)
        {
            return BoltFasteningState.SeatingIpm;
        }

        if (bolt is null)
        {
            var finalBolt = PendingIpmFinalBolts().FirstOrDefault();
            return (!gantry.AtSafeZ || !gantry.CanMoveHorizontal)
                && (finalBolt is null || !gantry.IsAt(finalBolt))
                ? BoltFasteningState.ClearingPickupHead
                : null;
        }

        if (!gantry.PickupBoltLoaded)
        {
            if (!gantry.AtPickupXY)
            {
                return !gantry.CanMoveHorizontal
                    ? BoltFasteningState.ClearingPickupHead
                    : BoltFasteningState.MovingToPickupXY;
            }

            if (gantry.PickupHeadPosition != BoltCylinderState.Down)
            {
                return !gantry.AtSafeZ
                    ? BoltFasteningState.MovingToPickupXY
                    : BoltFasteningState.LoweringForBoltPickup;
            }

            if (!gantry.AtPickupPosition)
            {
                return BoltFasteningState.MovingToPickupZ;
            }

            return pickupFeeder.State == BoltFeederState.BoltReady
                ? BoltFasteningState.PickingUpBolt
                : BoltFasteningState.WaitingForPickupFeeder;
        }

        if (gantry.AtPickupXY)
        {
            if (!gantry.AtSafeZ)
                return BoltFasteningState.RaisingPickedBolt;
            if (!gantry.CanMoveHorizontal)
                return BoltFasteningState.RaisingPickupHead;
        }

        if (!gantry.IsAt(bolt))
        {
            return !gantry.CanMoveHorizontal
                ? BoltFasteningState.ClearingPickupHead
                : BoltFasteningState.MovingToIpmSeatingBolt;
        }

        return gantry.PickupHeadPosition == BoltCylinderState.Down
            ? BoltFasteningState.SeatingIpm
            : BoltFasteningState.LoweringForIpmSeating;
    }

    private BoltFasteningState? IpmFinalState()
    {
        var bolt = PendingIpmFinalBolts().FirstOrDefault();
        if (gantry.HeadState(FasteningHead.Pickup) == BoltHeadState.Tightening)
        {
            return BoltFasteningState.FinalizingIpm;
        }

        if (bolt is null)
        {
            return gantry.AtSafeZ && gantry.CanMoveHorizontal
                ? null
                : BoltFasteningState.ClearingPickupHead;
        }

        if (!gantry.IsAt(bolt))
        {
            return !gantry.CanMoveHorizontal
                ? BoltFasteningState.ClearingPickupHead
                : BoltFasteningState.MovingToIpmFinalBolt;
        }

        return gantry.PickupHeadPosition == BoltCylinderState.Down
            ? BoltFasteningState.FinalizingIpm
            : BoltFasteningState.LoweringForIpmFinal;
    }

    private async Task FastenAsync(
        BoltFasteningRecipe recipe,
        FasteningPass pass,
        CancellationToken cancellationToken)
    {
        var (bolt, preset) = pass switch
        {
            FasteningPass.Pcb => (PendingPcbBolts().First(), recipe.PcbPreset),
            FasteningPass.IpmSeating => (PendingIpmSeatingBolts().First(), recipe.IpmSeatingPreset),
            FasteningPass.IpmFinal => (PendingIpmFinalBolts().First(), recipe.IpmFinalPreset),
            _ => throw new ArgumentOutOfRangeException(nameof(pass)),
        };
        var assembly = work.Assembly(bolt.HeatSink);
        if (gantry.HeadState(bolt.Head) == BoltHeadState.Ready)
        {
            await gantry.SelectPresetAsync(bolt.Head, preset, cancellationToken);
        }

        var result = await gantry.TightenHeadAsync(bolt.Head, cancellationToken);
        switch (pass)
        {
            case FasteningPass.Pcb:
                assembly.RecordPcbBolt(bolt.Number, result);
                break;
            case FasteningPass.IpmSeating:
                assembly.RecordIpmSeating(bolt.Number, result);
                break;
            case FasteningPass.IpmFinal:
                assembly.RecordIpmFinal(bolt.Number, result);
                break;
        }
    }

    private async Task LowerShootingHeadAsync(CancellationToken cancellationToken)
    {
        await gantry.SetPickupHeadDownAsync(false, cancellationToken);
        await gantry.SetHeadDownAsync(FasteningHead.Shooting, true, cancellationToken);
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
        foreach (var heatSink in Targets)
        {
            work.Assembly(heatSink).CompleteFastening();
        }

        await gantry.FinishFasteningAsync(FasteningHead.Pickup, cancellationToken);
        await gantry.FinishFasteningAsync(FasteningHead.Shooting, cancellationToken);
        await gantry.MoveToSafeZAsync(cancellationToken);

        work.Complete();
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
            .OrderBy(bolt => bolt.HeatSink)
            .ThenBy(bolt => bolt.Number);
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
