using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFeeder;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningProcess(
    BoltFasteningStation station,
    BoltFasteningWork work,
    PickupBoltFeeder pickupFeeder,
    LinearBoltFeeder linearFeeder)
{
    public void PrepareRecovery(
        IEnumerable<(HeatSinkSlot HeatSink, int Number, FasteningPass Pass)>
            completed)
    {
        station.DiscardPendingResults();
        work.PrepareRecovery(completed);
    }

    public async Task RunAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        using var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        work.Changed += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (work.State != BoltFasteningState.ReadyToFasten)
                {
                    await stateChanged.WaitAsync(cancellationToken);
                    continue;
                }

                using var stateOperation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                void ReevaluateState() => stateOperation.Cancel();

                work.Changed += ReevaluateState;
                try
                {
                    if (work.State == BoltFasteningState.ReadyToFasten)
                    {
                        await ExecuteAsync(recipe, stateOperation.Token);
                    }
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    work.Changed -= ReevaluateState;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            work.Changed -= OnStateChanged;
            station.Stop();
        }
    }

    private Task ExecuteAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken) => State(recipe) switch
    {
        BoltFasteningProcessState.FasteningPcb =>
            FastenPcbAsync(recipe, cancellationToken),
        BoltFasteningProcessState.MovingToPcbBolt =>
            MoveToPcbBoltAsync(recipe, cancellationToken),
        BoltFasteningProcessState.LoadingShootingBolt =>
            LoadShootingBoltAsync(cancellationToken),
        BoltFasteningProcessState.RetractingShootingEscape =>
            station.RetractShootingEscapeAsync(cancellationToken),
        BoltFasteningProcessState.ClearingShootingHead =>
            ClearHeadAsync(FasteningHead.Shooting, cancellationToken),
        BoltFasteningProcessState.MovingToPickupPosition =>
            MoveToPickupPositionAsync(cancellationToken),
        BoltFasteningProcessState.PickingUpBolt =>
            PickUpBoltAsync(cancellationToken),
        BoltFasteningProcessState.RaisingPickedBolt =>
            station.MoveToSafeZAsync(cancellationToken),
        BoltFasteningProcessState.MovingToIpmSeatingBolt =>
            MoveToIpmBoltAsync(
                PendingIpmSeatingBolts(recipe).First(),
                cancellationToken),
        BoltFasteningProcessState.LoweringForIpmSeating
            or BoltFasteningProcessState.LoweringForIpmFinal =>
            station.SetPickupHeadDownAsync(true, cancellationToken),
        BoltFasteningProcessState.SeatingIpm =>
            SeatIpmAsync(recipe, cancellationToken),
        BoltFasteningProcessState.MovingToIpmFinalBolt =>
            MoveToIpmBoltAsync(
                PendingIpmFinalBolts(recipe).First(),
                cancellationToken),
        BoltFasteningProcessState.FinalizingIpm =>
            FinalizeIpmAsync(recipe, cancellationToken),
        BoltFasteningProcessState.ClearingPickupHead =>
            ClearHeadAsync(FasteningHead.Pickup, cancellationToken),
        BoltFasteningProcessState.CompletingCarrier =>
            CompleteAsync(recipe, cancellationToken),
        _ => Task.CompletedTask,
    };

    public BoltFasteningProcessState State(BoltFasteningRecipe recipe)
    {
        if (work.State != BoltFasteningState.ReadyToFasten)
        {
            return BoltFasteningProcessState.Waiting;
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
            ?? BoltFasteningProcessState.CompletingCarrier;
    }

    public BoltPoint? ActiveBolt(BoltFasteningRecipe recipe) =>
        State(recipe) switch
        {
            BoltFasteningProcessState.MovingToPcbBolt
                or BoltFasteningProcessState.LoadingShootingBolt
                or BoltFasteningProcessState.RetractingShootingEscape
                or BoltFasteningProcessState.FasteningPcb =>
                PendingPcbBolts(recipe).FirstOrDefault(),
            BoltFasteningProcessState.MovingToIpmSeatingBolt
                or BoltFasteningProcessState.LoweringForIpmSeating
                or BoltFasteningProcessState.SeatingIpm =>
                PendingIpmSeatingBolts(recipe).FirstOrDefault(),
            BoltFasteningProcessState.MovingToIpmFinalBolt
                or BoltFasteningProcessState.LoweringForIpmFinal
                or BoltFasteningProcessState.FinalizingIpm =>
                PendingIpmFinalBolts(recipe).FirstOrDefault(),
            _ => null,
        };

    private BoltFasteningProcessState? PcbState(
        BoltFasteningRecipe recipe)
    {
        var bolt = PendingPcbBolts(recipe).FirstOrDefault();
        if (station.HeadState(FasteningHead.Shooting)
            == BoltHeadState.Tightening)
        {
            return BoltFasteningProcessState.FasteningPcb;
        }

        if (!station.AtSafeZ
            && (bolt is null || !station.IsAt(bolt))
            && station.ShootingHead != BoltCylinderState.Up)
        {
            return BoltFasteningProcessState.ClearingShootingHead;
        }

        if (bolt is null)
        {
            return null;
        }

        if (!station.IsAt(bolt))
        {
            return BoltFasteningProcessState.MovingToPcbBolt;
        }

        if (!station.ShootingBoltLoaded)
        {
            return BoltFasteningProcessState.LoadingShootingBolt;
        }

        return station.ShootingEscape == BoltEscapeState.Backward
            ? BoltFasteningProcessState.FasteningPcb
            : BoltFasteningProcessState.RetractingShootingEscape;
    }

    private BoltFasteningProcessState? IpmSeatingState(
        BoltFasteningRecipe recipe)
    {
        var bolt = PendingIpmSeatingBolts(recipe).FirstOrDefault();
        if (bolt is not null
            && station.HeadState(FasteningHead.Pickup)
            == BoltHeadState.Tightening)
        {
            return BoltFasteningProcessState.SeatingIpm;
        }

        if (bolt is null)
        {
            var finalBolt = PendingIpmFinalBolts(recipe)
                .FirstOrDefault();
            return !station.AtSafeZ
                   && (finalBolt is null || !station.IsAt(finalBolt))
                ? BoltFasteningProcessState.ClearingPickupHead
                : null;
        }

        if (!station.PickupBoltLoaded)
        {
            if (!station.AtSafeZ && !station.AtPickupPosition)
            {
                return BoltFasteningProcessState.ClearingPickupHead;
            }

            return station.AtPickupPosition
                ? BoltFasteningProcessState.PickingUpBolt
                : BoltFasteningProcessState.MovingToPickupPosition;
        }

        if (station.AtPickupXY && !station.AtSafeZ)
        {
            return BoltFasteningProcessState.RaisingPickedBolt;
        }

        if (!station.IsAt(bolt))
        {
            return !station.AtSafeZ && !station.IsAtXY(bolt)
                ? BoltFasteningProcessState.ClearingPickupHead
                : BoltFasteningProcessState.MovingToIpmSeatingBolt;
        }

        return station.PickupHead == BoltCylinderState.Down
            ? BoltFasteningProcessState.SeatingIpm
            : BoltFasteningProcessState.LoweringForIpmSeating;
    }

    private BoltFasteningProcessState? IpmFinalState(
        BoltFasteningRecipe recipe)
    {
        var bolt = PendingIpmFinalBolts(recipe).FirstOrDefault();
        if (station.HeadState(FasteningHead.Pickup)
            == BoltHeadState.Tightening)
        {
            return BoltFasteningProcessState.FinalizingIpm;
        }

        if (bolt is null)
        {
            return station.AtSafeZ
                ? null
                : BoltFasteningProcessState.ClearingPickupHead;
        }

        if (!station.IsAt(bolt))
        {
            return !station.AtSafeZ
                ? BoltFasteningProcessState.ClearingPickupHead
                : BoltFasteningProcessState.MovingToIpmFinalBolt;
        }

        return station.PickupHead == BoltCylinderState.Down
            ? BoltFasteningProcessState.FinalizingIpm
            : BoltFasteningProcessState.LoweringForIpmFinal;
    }

    private async Task FastenPcbAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken)
    {
        var bolt = PendingPcbBolts(recipe).First();
        if (station.HeadState(FasteningHead.Shooting)
            == BoltHeadState.Ready)
        {
            await station.SelectHeadAsync(
                FasteningHead.Shooting,
                cancellationToken);
            await station.SelectPresetAsync(
                FasteningHead.Shooting,
                recipe.PcbPreset,
                cancellationToken);
        }

        var result = await station.TightenHeadAsync(
            FasteningHead.Shooting,
            cancellationToken);
        work.Assembly(bolt.HeatSink).RecordPcbBolt(
            bolt.Number,
            result);
    }

    private async Task MoveToPcbBoltAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken)
    {
        var bolt = PendingPcbBolts(recipe).First();
        await station.SelectHeadAsync(
            FasteningHead.Shooting,
            cancellationToken);
        await station.MoveToBoltAsync(bolt, cancellationToken);
    }

    private async Task LoadShootingBoltAsync(
        CancellationToken cancellationToken)
    {
        await station.SelectHeadAsync(
            FasteningHead.Shooting,
            cancellationToken);
        await linearFeeder.WaitUntilReadyAsync(cancellationToken);
        await station.LoadShootingBoltAsync(cancellationToken);
    }

    private async Task SeatIpmAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken)
    {
        var bolt = PendingIpmSeatingBolts(recipe).First();
        if (station.HeadState(FasteningHead.Pickup)
            == BoltHeadState.Ready)
        {
            await station.SelectPresetAsync(
                FasteningHead.Pickup,
                recipe.IpmSeatingPreset,
                cancellationToken);
        }

        var result = await station.TightenHeadAsync(
            FasteningHead.Pickup,
            cancellationToken);
        work.Assembly(bolt.HeatSink).RecordIpmSeating(
            bolt.Number,
            result);
    }

    private async Task FinalizeIpmAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken)
    {
        var bolt = PendingIpmFinalBolts(recipe).First();
        if (station.HeadState(FasteningHead.Pickup)
            == BoltHeadState.Ready)
        {
            await station.SelectPresetAsync(
                FasteningHead.Pickup,
                recipe.IpmFinalPreset,
                cancellationToken);
        }

        var result = await station.TightenHeadAsync(
            FasteningHead.Pickup,
            cancellationToken);
        work.Assembly(bolt.HeatSink).RecordIpmFinal(
            bolt.Number,
            result);
    }

    private async Task MoveToPickupPositionAsync(
        CancellationToken cancellationToken)
    {
        await station.SelectHeadAsync(
            FasteningHead.Pickup,
            cancellationToken);
        await station.MoveToPickupPositionAsync(cancellationToken);
    }

    private async Task PickUpBoltAsync(
        CancellationToken cancellationToken)
    {
        await station.SelectHeadAsync(
            FasteningHead.Pickup,
            cancellationToken);
        await pickupFeeder.WaitUntilReadyAsync(cancellationToken);
        await station.PickUpBoltAsync(cancellationToken);
    }

    private async Task MoveToIpmBoltAsync(
        BoltPoint bolt,
        CancellationToken cancellationToken)
    {
        await station.SelectHeadAsync(
            FasteningHead.Pickup,
            cancellationToken);
        await station.MoveToBoltAsync(bolt, cancellationToken);
    }

    private async Task ClearHeadAsync(
        FasteningHead head,
        CancellationToken cancellationToken)
    {
        await station.FinishFasteningAsync(head, cancellationToken);
        await station.MoveToSafeZAsync(cancellationToken);
    }

    private async Task CompleteAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken)
    {
        foreach (var heatSink in Enum.GetValues<HeatSinkSlot>()
                     .Where(work.HeatSinkPresent))
        {
            work.Assembly(heatSink).CompleteFastening();
        }

        await station.FinishFasteningAsync(
            FasteningHead.Pickup,
            cancellationToken);
        await station.FinishFasteningAsync(
            FasteningHead.Shooting,
            cancellationToken);
        await station.RaiseShootingHeadAsync(cancellationToken);
        if (ApplicableBolts(recipe).Any())
        {
            await station.MoveToSafeZAsync(cancellationToken);
        }

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

    private PcbAssembly? FindAssembly(HeatSinkSlot heatSink) =>
        work.Assemblies.FirstOrDefault(
            assembly => assembly.HeatSink == heatSink);

    private IEnumerable<BoltPoint> ApplicableBolts(
        BoltFasteningRecipe recipe) =>
        recipe.BoltPoints
            .Where(bolt => work.HeatSinkPresent(bolt.HeatSink))
            .OrderBy(bolt => bolt.Number);
}
