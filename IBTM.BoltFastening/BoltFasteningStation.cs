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
    ShootingBoltFeeder shootingFeeder)
{
    private HeatSinkSlot[]? _runTargets;

    public void PrepareRecovery(
        IEnumerable<(HeatSinkSlot HeatSink, int Number, FasteningPass Pass, bool Completed)>
            items)
    {
        gantry.DiscardPendingResults();
        work.PrepareRecovery(items);
    }

    public async Task RunAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        work.Changed += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (work.State != BoltFasteningWorkState.ReadyToFasten)
                {
                    await stateChanged.WaitAsync(cancellationToken);
                    continue;
                }

                using var carrierOperation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                void CheckCarrier()
                {
                    if (!work.CarrierSeated) carrierOperation.Cancel();
                }

                _runTargets = Enum.GetValues<HeatSinkSlot>()
                    .Where(work.HeatSinkPresent)
                    .ToArray();
                work.Changed += CheckCarrier;
                try
                {
                    CheckCarrier();
                    while (work.State == BoltFasteningWorkState.ReadyToFasten)
                    {
                        carrierOperation.Token.ThrowIfCancellationRequested();
                        await ExecuteAsync(recipe, carrierOperation.Token);
                    }
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    work.Changed -= CheckCarrier;
                    _runTargets = null;
                    gantry.DiscardPendingResults();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            work.Changed -= OnStateChanged;
            gantry.StopShooting();
        }
    }

    private Task ExecuteAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken) => State(recipe) switch
        {
            BoltFasteningState.FasteningPcb =>
                FastenAsync(recipe, FasteningPass.Pcb, cancellationToken),
            BoltFasteningState.MovingToPcbBolt =>
                MoveToBoltAsync(
                    PendingPcbBolts(recipe).First(),
                    cancellationToken),
            BoltFasteningState.LoweringForPcb =>
                gantry.SetHeadDownAsync(
                    FasteningHead.Shooting,
                    true,
                    cancellationToken),
            BoltFasteningState.LoadingShootingBolt =>
                LoadShootingBoltAsync(cancellationToken),
            BoltFasteningState.WaitingForShootingTubeClear =>
                gantry.WaitForShootingTubeClearAsync(cancellationToken),
            BoltFasteningState.RetractingShootingEscape =>
                gantry.RetractShootingEscapeAsync(cancellationToken),
            BoltFasteningState.ClearingShootingHead =>
                ClearHeadAsync(FasteningHead.Shooting, cancellationToken),
            BoltFasteningState.MovingToPickupPosition =>
                MoveToPickupPositionAsync(cancellationToken),
            BoltFasteningState.PickingUpBolt =>
                PickUpBoltAsync(cancellationToken),
            BoltFasteningState.RaisingPickedBolt =>
                gantry.MoveToSafeZAsync(cancellationToken),
            BoltFasteningState.MovingToIpmSeatingBolt =>
                MoveToBoltAsync(
                    PendingIpmSeatingBolts(recipe).First(),
                    cancellationToken),
            BoltFasteningState.LoweringForIpmSeating
                or BoltFasteningState.LoweringForIpmFinal =>
                gantry.SetPickupHeadDownAsync(true, cancellationToken),
            BoltFasteningState.SeatingIpm =>
                FastenAsync(recipe, FasteningPass.IpmSeating, cancellationToken),
            BoltFasteningState.MovingToIpmFinalBolt =>
                MoveToBoltAsync(
                    PendingIpmFinalBolts(recipe).First(),
                    cancellationToken),
            BoltFasteningState.FinalizingIpm =>
                FastenAsync(recipe, FasteningPass.IpmFinal, cancellationToken),
            BoltFasteningState.ClearingPickupHead =>
                ClearHeadAsync(FasteningHead.Pickup, cancellationToken),
            BoltFasteningState.CompletingCarrier =>
                CompleteAsync(cancellationToken),
            _ => Task.CompletedTask,
        };

    public BoltFasteningState State(BoltFasteningRecipe recipe)
    {
        if (work.State != BoltFasteningWorkState.ReadyToFasten)
        {
            return BoltFasteningState.Waiting;
        }

        if (PcbState(recipe) is { } pcbState)
        {
            return pcbState;
        }

        if (IpmSeatingState(recipe) is { } seatingState)
        {
            return seatingState;
        }

        return IpmFinalState(recipe)
            ?? BoltFasteningState.CompletingCarrier;
    }

    public BoltPoint? ActiveBolt(BoltFasteningRecipe recipe) =>
        State(recipe) switch
        {
            BoltFasteningState.MovingToPcbBolt
                or BoltFasteningState.LoweringForPcb
                or BoltFasteningState.LoadingShootingBolt
                or BoltFasteningState.WaitingForShootingTubeClear
                or BoltFasteningState.RetractingShootingEscape
                or BoltFasteningState.FasteningPcb =>
                PendingPcbBolts(recipe).FirstOrDefault(),
            BoltFasteningState.MovingToIpmSeatingBolt
                or BoltFasteningState.LoweringForIpmSeating
                or BoltFasteningState.SeatingIpm =>
                PendingIpmSeatingBolts(recipe).FirstOrDefault(),
            BoltFasteningState.MovingToIpmFinalBolt
                or BoltFasteningState.LoweringForIpmFinal
                or BoltFasteningState.FinalizingIpm =>
                PendingIpmFinalBolts(recipe).FirstOrDefault(),
            _ => null,
        };

    private BoltFasteningState? PcbState(
        BoltFasteningRecipe recipe)
    {
        var bolt = PendingPcbBolts(recipe).FirstOrDefault();
        if (gantry.HeadState(FasteningHead.Shooting)
            == BoltHeadState.Tightening)
        {
            return BoltFasteningState.FasteningPcb;
        }

        if ((bolt is null || !gantry.IsAt(bolt))
            && !gantry.CanMoveHorizontal)
        {
            return BoltFasteningState.ClearingShootingHead;
        }

        if (bolt is null)
        {
            return null;
        }

        if (!gantry.IsAt(bolt))
        {
            return BoltFasteningState.MovingToPcbBolt;
        }

        if (gantry.ShootingHeadPosition != BoltCylinderState.Down)
        {
            return BoltFasteningState.LoweringForPcb;
        }

        if (!gantry.ShootingBoltLoaded)
        {
            return BoltFasteningState.LoadingShootingBolt;
        }

        if (gantry.ShootingTubeBoltDetected)
        {
            return BoltFasteningState.WaitingForShootingTubeClear;
        }

        return gantry.ShootingEscape == BoltEscapeState.Backward
            ? BoltFasteningState.FasteningPcb
            : BoltFasteningState.RetractingShootingEscape;
    }

    private BoltFasteningState? IpmSeatingState(
        BoltFasteningRecipe recipe)
    {
        var bolt = PendingIpmSeatingBolts(recipe).FirstOrDefault();
        if (bolt is not null
            && gantry.HeadState(FasteningHead.Pickup)
            == BoltHeadState.Tightening)
        {
            return BoltFasteningState.SeatingIpm;
        }

        if (bolt is null)
        {
            var finalBolt = PendingIpmFinalBolts(recipe)
                .FirstOrDefault();
            return !gantry.AtSafeZ
                   && (finalBolt is null || !gantry.IsAt(finalBolt))
                ? BoltFasteningState.ClearingPickupHead
                : null;
        }

        if (!gantry.PickupBoltLoaded)
        {
            if (!gantry.AtPickupPosition && !gantry.CanMoveHorizontal)
            {
                return BoltFasteningState.ClearingPickupHead;
            }

            return gantry.AtPickupPosition
                ? BoltFasteningState.PickingUpBolt
                : BoltFasteningState.MovingToPickupPosition;
        }

        if (gantry.AtPickupXY && !gantry.AtSafeZ)
        {
            return BoltFasteningState.RaisingPickedBolt;
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

    private BoltFasteningState? IpmFinalState(
        BoltFasteningRecipe recipe)
    {
        var bolt = PendingIpmFinalBolts(recipe).FirstOrDefault();
        if (gantry.HeadState(FasteningHead.Pickup)
            == BoltHeadState.Tightening)
        {
            return BoltFasteningState.FinalizingIpm;
        }

        if (bolt is null)
        {
            return gantry.AtSafeZ
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
            FasteningPass.Pcb =>
                (PendingPcbBolts(recipe).First(), recipe.PcbPreset),
            FasteningPass.IpmSeating =>
                (PendingIpmSeatingBolts(recipe).First(), recipe.IpmSeatingPreset),
            FasteningPass.IpmFinal =>
                (PendingIpmFinalBolts(recipe).First(), recipe.IpmFinalPreset),
            _ => throw new ArgumentOutOfRangeException(nameof(pass)),
        };
        var assembly = work.Assembly(bolt.HeatSink);
        if (gantry.HeadState(bolt.Head) == BoltHeadState.Ready)
        {
            await gantry.SelectPresetAsync(
                bolt.Head,
                preset,
                cancellationToken);
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

    private async Task LoadShootingBoltAsync(
        CancellationToken cancellationToken)
    {
        await shootingFeeder.WaitUntilReadyAsync(cancellationToken);
        await gantry.LoadShootingBoltAsync(cancellationToken);
    }

    private async Task MoveToPickupPositionAsync(
        CancellationToken cancellationToken)
    {
        await gantry.RaiseCylindersAsync(cancellationToken);
        await gantry.MoveToPickupPositionAsync(cancellationToken);
    }

    private async Task PickUpBoltAsync(
        CancellationToken cancellationToken)
    {
        await gantry.RaiseCylindersAsync(cancellationToken);
        await pickupFeeder.WaitUntilReadyAsync(cancellationToken);
        await gantry.PickUpBoltAsync(cancellationToken);
    }

    private async Task MoveToBoltAsync(
        BoltPoint bolt,
        CancellationToken cancellationToken)
    {
        await gantry.RaiseCylindersAsync(cancellationToken);
        await gantry.MoveToBoltAsync(bolt, cancellationToken);
    }

    private async Task ClearHeadAsync(
        FasteningHead head,
        CancellationToken cancellationToken)
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

        await gantry.FinishFasteningAsync(
            FasteningHead.Pickup,
            cancellationToken);
        await gantry.FinishFasteningAsync(
            FasteningHead.Shooting,
            cancellationToken);
        await gantry.MoveToSafeZAsync(cancellationToken);

        work.Complete();
    }

    private IEnumerable<BoltPoint> PendingPcbBolts(
        BoltFasteningRecipe recipe) =>
        ApplicableBolts(recipe)
            .Where(bolt => bolt.Head == FasteningHead.Shooting)
            .Where(bolt => FindAssembly(bolt.HeatSink)?
                .PcbBoltResults.ContainsKey(bolt.Number) != true);

    private IEnumerable<BoltPoint> PendingIpmSeatingBolts(
        BoltFasteningRecipe recipe) =>
        ApplicableBolts(recipe)
            .Where(bolt => bolt.Head == FasteningHead.Pickup)
            .Where(bolt => FindAssembly(bolt.HeatSink)?
                .IpmSeatingResults.ContainsKey(bolt.Number) != true);

    private IEnumerable<BoltPoint> PendingIpmFinalBolts(
        BoltFasteningRecipe recipe) =>
        ApplicableBolts(recipe)
            .Where(bolt => bolt.Head == FasteningHead.Pickup)
            .Where(bolt => FindAssembly(bolt.HeatSink)?
                .IpmFinalResults.ContainsKey(bolt.Number) != true);

    private HeatSinkAssembly? FindAssembly(HeatSinkSlot heatSink) =>
        work.Assemblies.FirstOrDefault(
            assembly => assembly.HeatSink == heatSink);

    private IEnumerable<BoltPoint> ApplicableBolts(
        BoltFasteningRecipe recipe) =>
        recipe.BoltPoints
            .Where(bolt => Targets.Contains(bolt.HeatSink))
            .OrderBy(bolt => bolt.Number);

    private IEnumerable<HeatSinkSlot> Targets =>
        _runTargets is { } targets
            ? targets
            : Enum.GetValues<HeatSinkSlot>().Where(work.HeatSinkPresent);
}
