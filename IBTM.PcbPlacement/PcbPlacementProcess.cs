using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.PcbBuffer;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementProcess(
    BufferStage buffer,
    PcbPlacementHandler handler,
    PcbPlacementWork work)
{
    private bool CanPickFromBuffer =>
        handler.Pcb == PlacementPcbState.Secured
            && buffer.PlacementBlocksSupply
        || handler.Pcb != PlacementPcbState.Secured
            && buffer.CanPlacementEnter;

    public async Task RunAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        using var stateChanged = new AsyncAutoResetEvent();
        HousingSlot? completedHousing = null;
        void OnStateChanged() => stateChanged.Set();
        void OnCarrierChanged(bool _)
        {
            completedHousing = null;
        }

        handler.Changed += OnStateChanged;
        buffer.StateChanged += OnStateChanged;
        work.Changed += OnStateChanged;
        work.CarrierChanged += OnCarrierChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (work.Ready
                    && !work.Completed
                    && NextHousing(completedHousing) is null)
                {
                    work.Complete();
                    continue;
                }

                if (handler.Pcb == PlacementPcbState.Secured)
                {
                    if (!handler.IsWaitingAbove(
                            recipe.Housing1PcbPlacementPosition))
                    {
                        await PickFromBufferCoreAsync(
                            recipe,
                            cancellationToken);
                        continue;
                    }

                    var housing = work.Ready
                        ? NextHousing(completedHousing)
                        : null;
                    if (housing is not null)
                    {
                        await handler.PlaceAsync(
                            HousingPosition(recipe, housing.Value),
                            cancellationToken);
                        work.Assembly(housing.Value);
                        completedHousing = housing;
                        continue;
                    }
                }
                else if (CanPickFromBuffer)
                {
                    await PickFromBufferCoreAsync(
                        recipe,
                        cancellationToken);
                    continue;
                }

                await stateChanged.WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            handler.Changed -= OnStateChanged;
            buffer.StateChanged -= OnStateChanged;
            work.Changed -= OnStateChanged;
            work.CarrierChanged -= OnCarrierChanged;
        }
    }

    private async Task PickFromBufferCoreAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken)
    {
        if (handler.Pcb != PlacementPcbState.Secured)
        {
            await handler.SecureAtBufferAsync(cancellationToken);
        }

        await buffer.WaitForSupplyOutsideAsync(cancellationToken);
        await handler.ClearBufferAsync(cancellationToken);
        await handler.WaitAboveHousingAsync(
            recipe.Housing1PcbPlacementPosition,
            cancellationToken);
    }

    private HousingSlot? NextHousing(HousingSlot? completedHousing)
    {
        if (completedHousing is null
            && work.HousingPresent(HousingSlot.Housing1))
        {
            return HousingSlot.Housing1;
        }

        return completedHousing != HousingSlot.Housing2
            && work.HousingPresent(HousingSlot.Housing2)
                ? HousingSlot.Housing2
                : null;
    }

    private static AxisPos HousingPosition(
        PcbPlacementRecipe recipe,
        HousingSlot housing) =>
        housing == HousingSlot.Housing1
            ? recipe.Housing1PcbPlacementPosition
            : recipe.Housing2PcbPlacementPosition;
}
