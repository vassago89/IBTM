using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.PcbBuffer;

namespace IBTM.PcbSupply;

public sealed class PcbSupplier(
    PcbSupplyHandler handler,
    BufferStage buffer)
{
    public bool CanHome =>
        handler.CanPrepareHome
        && (handler.Rotation != PcbSupplyRotationState.Unrotated
            || !buffer.PcbPresent);

    public async Task RunAsync(
        PcbSupplyRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        var pickStep = PickStep.Pcb1;
        var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged()
        {
            if (pickStep == PickStep.WaitingForCarrierExit
                && !handler.UpstreamCarrierAvailable)
            {
                pickStep = PickStep.Pcb1;
            }

            stateChanged.Set();
        }

        handler.Changed += OnStateChanged;
        buffer.StateChanged += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (pickStep != PickStep.WaitingForCarrierExit)
                {
                    handler.SetUpstreamReady(
                        pickStep == PickStep.Pcb1
                        && !handler.UpstreamCarrierAvailable);
                }
                else if (handler.IsAtRotationZ
                         && handler.Pcb != PcbSupplyPcbState.Detected)
                {
                    handler.SetUpstreamReady(true);
                }

                switch (State(pickStep))
                {
                    case PcbSupplyState.PickingPcb:
                        await handler.PickAsync(
                            pickStep == PickStep.Pcb1
                                ? recipe.Pcb1PickPosition
                                : recipe.Pcb2PickPosition,
                            cancellationToken);
                        pickStep = pickStep == PickStep.Pcb1
                            ? PickStep.Pcb2
                            : PickStep.WaitingForCarrierExit;
                        break;

                    case PcbSupplyState.SecuringPcb:
                        await handler.SecurePcbAsync(cancellationToken);
                        break;

                    case PcbSupplyState.RaisingForPickup:
                        await handler.MoveToRotationZAsync(
                            cancellationToken);
                        break;

                    case PcbSupplyState.MovingAboveBuffer:
                        await handler.MoveAboveHandoffAsync(
                            cancellationToken);
                        break;

                    case PcbSupplyState.RotatingForBuffer:
                        await handler.SetRotatedAsync(
                            true,
                            cancellationToken);
                        break;

                    case PcbSupplyState.MovingToBuffer:
                        await handler.LowerToHandoffAsync(cancellationToken);
                        break;

                    case PcbSupplyState.WaitingForBufferPcb:
                        await buffer.WaitForPcbAsync(
                            true,
                            cancellationToken);
                        break;

                    case PcbSupplyState.ReleasingPcb:
                        await handler.SetIpmFixerAsync(
                            false,
                            cancellationToken);
                        await handler.SetNestAsync(
                            false,
                            cancellationToken);
                        await handler.MoveClearAsync(cancellationToken);
                        break;

                    case PcbSupplyState.UnrotatingForPickup:
                        await handler.SetRotatedAsync(
                            false,
                            cancellationToken);
                        break;

                    default:
                        await stateChanged.WaitAsync(cancellationToken);
                        break;
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
            handler.SetUpstreamReady(false);
        }
    }

    private PcbSupplyState State(PickStep pickStep)
    {
        var pcb = handler.Pcb;
        var rotation = handler.Rotation;

        if (buffer.SupplyAtHandoff)
        {
            if (!buffer.PcbPresent)
            {
                return PcbSupplyState.WaitingForBufferPcb;
            }

            return buffer.PlacementSecuredAtHandoff
                ? PcbSupplyState.ReleasingPcb
                : PcbSupplyState.WaitingForPlacement;
        }

        if (buffer.SupplyInside && pcb != PcbSupplyPcbState.Secured)
        {
            return PcbSupplyState.ReleasingPcb;
        }

        if (pcb == PcbSupplyPcbState.Detected)
        {
            return PcbSupplyState.SecuringPcb;
        }

        if (pcb == PcbSupplyPcbState.Secured)
        {
            if (rotation != PcbSupplyRotationState.Rotated)
            {
                return PcbSupplyState.RotatingForBuffer;
            }

            if (!handler.AtHandoffXY)
            {
                return buffer.CanSupplyEnter
                    ? PcbSupplyState.MovingAboveBuffer
                    : PcbSupplyState.WaitingForBuffer;
            }

            return (buffer.CanSupplyEnter
                    || handler.InHandoffZRange && buffer.CanSupplyLower)
                ? PcbSupplyState.MovingToBuffer
                : PcbSupplyState.WaitingForBuffer;
        }

        if (!handler.IsAtRotationZ)
        {
            return PcbSupplyState.RaisingForPickup;
        }

        if (rotation != PcbSupplyRotationState.Unrotated)
        {
            return PcbSupplyState.UnrotatingForPickup;
        }

        if (pickStep == PickStep.WaitingForCarrierExit)
        {
            return PcbSupplyState.WaitingForCarrierExit;
        }

        return handler.UpstreamCarrierAvailable
            ? PcbSupplyState.PickingPcb
            : PcbSupplyState.WaitingForCarrier;
    }

    private enum PickStep
    {
        Pcb1,
        Pcb2,
        WaitingForCarrierExit,
    }
}
