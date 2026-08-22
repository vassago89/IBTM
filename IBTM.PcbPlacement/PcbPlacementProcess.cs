using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbSupply;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementProcess(
    BufferStage buffer,
    PcbPlacementHandler handler,
    PcbSupplyProcess supply,
    OperationCancellation operations)
{
    public bool CanPickFromBuffer =>
        !handler.IsMoving
        && ((handler.PcbSecured
                && buffer.PlacementInside
                && buffer.CanPlacementExit)
            || (!handler.PcbSecured
                && buffer.CanPlacementEnter
                && (!buffer.SupplyInside || supply.ReadyForHandoff)));

    public async Task PickFromBufferAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        using var operation = operations.Link(cancellationToken);
        try
        {
            await PickFromBufferCoreAsync(recipe, operation.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task RunAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        var stateChanged = new SemaphoreSlim(0);
        void OnStateChanged() => stateChanged.Release();

        handler.Changed += OnStateChanged;
        buffer.StateChanged += OnStateChanged;
        supply.Changed += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (CanPickFromBuffer
                    || handler.PcbSecured && buffer.PlacementInside)
                {
                    await PickFromBufferCoreAsync(
                        recipe,
                        cancellationToken);
                }
                else
                {
                    await stateChanged.WaitAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            handler.Changed -= OnStateChanged;
            buffer.StateChanged -= OnStateChanged;
            supply.Changed -= OnStateChanged;
        }
    }

    private async Task PickFromBufferCoreAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken)
    {
        if (!handler.PcbSecured)
        {
            await handler.SecureAtBufferAsync(cancellationToken);
        }

        if (buffer.SupplyInside)
        {
            await supply.ReleaseToPlacementAsync(cancellationToken);
        }

        await handler.MoveClearAsync(
            recipe.Fiducial1Position,
            cancellationToken);
    }
}
