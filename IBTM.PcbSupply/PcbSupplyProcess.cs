using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyProcess(
    PcbSupplyHandler handler,
    BufferStage buffer)
{
    public bool CanHome =>
        handler.CanPrepareHome
        && (handler.Rotation != PcbSupplyRotation.Unrotated
            || !buffer.PcbPresent);

    private bool CanMoveToBuffer =>
        handler.Rotation == PcbSupplyRotation.Rotated
        && handler.Pcb == PcbSupplyPcbState.Secured
        && buffer.CanSupplyEnter;

    public async Task RunAsync(
        PcbSupplyRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        using var stateChanged = new AsyncAutoResetEvent();
        var step = (handler.Pcb == PcbSupplyPcbState.Secured
                    || handler.Rotation != PcbSupplyRotation.Unrotated)
            ? PcbSupplyStep.ResolvingHandler
            : NextCarrierStep();

        void OnStateChanged() => stateChanged.Set();

        handler.Changed += OnStateChanged;
        buffer.StateChanged += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (handler.Rotation == PcbSupplyRotation.Between)
                {
                    await stateChanged.WaitAsync(cancellationToken);
                    continue;
                }

                if (handler.Rotation == PcbSupplyRotation.Rotated
                    && buffer.SupplyInside
                    && buffer.PlacementSecuredAtHandoff)
                {
                    await ReleaseToPlacementAsync(cancellationToken);
                    continue;
                }

                if (handler.Pcb == PcbSupplyPcbState.Secured)
                {
                    if (handler.Rotation == PcbSupplyRotation.Unrotated)
                    {
                        await handler.SetRotatedAsync(
                            true,
                            cancellationToken);
                    }
                    else if (CanMoveToBuffer)
                    {
                        await MoveToBufferCoreAsync(cancellationToken);
                    }
                    else
                    {
                        await stateChanged.WaitAsync(cancellationToken);
                    }

                    continue;
                }

                if (handler.Rotation == PcbSupplyRotation.Rotated)
                {
                    if (buffer.SupplyInside)
                    {
                        await stateChanged.WaitAsync(cancellationToken);
                    }
                    else
                    {
                        await handler.SetRotatedAsync(
                            false,
                            cancellationToken);
                        if (step == PcbSupplyStep.Pcb1)
                        {
                            step = PcbSupplyStep.Pcb2;
                        }
                        else if (step == PcbSupplyStep.ResolvingHandler)
                        {
                            step = NextCarrierStep();
                        }
                    }

                    continue;
                }

                if (step == PcbSupplyStep.ResolvingHandler)
                {
                    step = NextCarrierStep();
                    continue;
                }

                if (step == PcbSupplyStep.WaitingForCarrierExit)
                {
                    if (handler.UpstreamCarrierAvailable)
                    {
                        await stateChanged.WaitAsync(cancellationToken);
                        continue;
                    }

                    step = PcbSupplyStep.WaitingForCarrier;
                }

                if (step == PcbSupplyStep.WaitingForCarrier)
                {
                    handler.SetUpstreamReady(true);
                    if (!handler.UpstreamCarrierAvailable)
                    {
                        await stateChanged.WaitAsync(cancellationToken);
                        continue;
                    }

                    handler.SetUpstreamReady(false);
                    step = PcbSupplyStep.Pcb1;
                    continue;
                }

                var pcbDetected = await handler.PickAsync(
                    step == PcbSupplyStep.Pcb1
                        ? recipe.Pcb1PickPosition
                        : recipe.Pcb2PickPosition,
                    cancellationToken);
                if (step == PcbSupplyStep.Pcb1)
                {
                    if (!pcbDetected)
                    {
                        step = PcbSupplyStep.Pcb2;
                    }

                    continue;
                }

                step = handler.UpstreamCarrierAvailable
                    ? PcbSupplyStep.WaitingForCarrierExit
                    : PcbSupplyStep.WaitingForCarrier;
                handler.SetUpstreamReady(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            handler.Changed -= OnStateChanged;
            buffer.StateChanged -= OnStateChanged;
            handler.SetUpstreamReady(false);
        }
    }

    private async Task MoveToBufferCoreAsync(
        CancellationToken cancellationToken)
    {
        await handler.MoveToHandoffAsync(cancellationToken);
        await buffer.WaitForPcbAsync(true, cancellationToken);
    }

    private async Task ReleaseToPlacementAsync(
        CancellationToken cancellationToken)
    {
        await handler.SetIpmFixerAsync(false, cancellationToken);
        await handler.SetNestAsync(false, cancellationToken);
        await handler.MoveClearAsync(cancellationToken);
    }

    private PcbSupplyStep NextCarrierStep() =>
        handler.UpstreamCarrierAvailable
            ? PcbSupplyStep.Pcb1
            : PcbSupplyStep.WaitingForCarrier;

    private enum PcbSupplyStep
    {
        ResolvingHandler,
        WaitingForCarrier,
        Pcb1,
        Pcb2,
        WaitingForCarrierExit,
    }
}
