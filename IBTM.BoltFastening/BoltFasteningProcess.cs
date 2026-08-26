using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFeeder;
using IBTM.Core;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningProcess(
    BoltFasteningStation station,
    BoltFasteningWork work,
    PickupBoltFeeder pickupFeeder,
    LinearBoltFeeder linearFeeder)
{
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
                        await FastenCarrierAsync(
                            recipe,
                            stateOperation.Token);
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
        }
    }

    private async Task FastenCarrierAsync(
        BoltFasteningRecipe recipe,
        CancellationToken cancellationToken)
    {
        var bolts = recipe.BoltPoints
            .OrderBy(bolt => bolt.Number)
            .Where(bolt => work.HousingPresent(bolt.Housing))
            .ToArray();
        var pcbBolts = bolts
            .Where(bolt => bolt.Head == FasteningHead.Shooting)
            .ToArray();
        var ipmBolts = bolts
            .Where(bolt => bolt.Head == FasteningHead.Pickup)
            .ToArray();
        var assemblies = Enum.GetValues<HousingSlot>()
            .Where(work.HousingPresent)
            .Select(work.Assembly)
            .ToArray();

        if (pcbBolts.Length > 0)
        {
            await linearFeeder.WaitUntilReadyAsync(cancellationToken);
        }

        if (ipmBolts.Length > 0)
        {
            await pickupFeeder.WaitUntilReadyAsync(cancellationToken);
        }

        foreach (var assembly in assemblies)
        {
            assembly.BeginFastening();
        }

        if (pcbBolts.Length > 0)
        {
            await station.SelectHeadAsync(
                FasteningHead.Shooting,
                recipe.PcbPreset,
                cancellationToken);

            foreach (var bolt in pcbBolts)
            {
                if (!station.ShootingBoltLoaded)
                {
                    await linearFeeder.WaitUntilReadyAsync(cancellationToken);
                    await station.LoadShootingBoltAsync(cancellationToken);
                }

                var result = await station.FastenAsync(
                    bolt,
                    cancellationToken);
                work.Assembly(bolt.Housing).RecordPcbBolt(
                    bolt.Number,
                    result);
            }

            await station.ReleaseHeadAsync(
                FasteningHead.Shooting,
                cancellationToken);
        }

        if (ipmBolts.Length > 0)
        {
            await station.SelectHeadAsync(
                FasteningHead.Pickup,
                recipe.IpmSeatingPreset,
                cancellationToken);

            foreach (var bolt in ipmBolts)
            {
                if (!station.PickupBoltLoaded)
                {
                    await pickupFeeder.WaitUntilReadyAsync(cancellationToken);
                    await station.LoadPickupBoltAsync(cancellationToken);
                }

                var result = await station.FastenAsync(
                    bolt,
                    cancellationToken);
                work.Assembly(bolt.Housing).RecordIpmSeating(
                    bolt.Number,
                    result);
            }

            await station.SelectHeadAsync(
                FasteningHead.Pickup,
                recipe.IpmFinalPreset,
                cancellationToken);

            foreach (var bolt in ipmBolts)
            {
                var result = await station.FastenAsync(
                    bolt,
                    cancellationToken);
                work.Assembly(bolt.Housing).RecordIpmFinal(
                    bolt.Number,
                    result);
            }

            await station.ReleaseHeadAsync(
                FasteningHead.Pickup,
                cancellationToken);
        }

        foreach (var assembly in assemblies)
        {
            assembly.CompleteFastening();
        }

        if (bolts.Length > 0)
        {
            await station.MoveToSafeZAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        work.Complete();
    }
}
