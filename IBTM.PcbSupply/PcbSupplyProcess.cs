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
        && (handler.Rotation != PcbSupplyRotationState.Unrotated
            || !buffer.PcbPresent);

    public async Task RunAsync(
        PcbSupplyRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        var pickStep = PickStep.Pcb1;
        var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        handler.Changed += OnStateChanged;
        buffer.StateChanged += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var waitingForCarrierExit =
                    pickStep == PickStep.WaitingForCarrierExit
                    && handler.Pcb == PcbSupplyPcbState.None
                    && handler.Rotation == PcbSupplyRotationState.Unrotated;
                var waitingForFirstCarrier = pickStep == PickStep.Pcb1
                    && !handler.UpstreamCarrierAvailable;
                handler.SetUpstreamReady(
                    waitingForCarrierExit || waitingForFirstCarrier);

                var state = State(pickStep);
                switch (state)
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

                    case PcbSupplyState.WaitingForCarrierExit:
                        await handler.WaitForUpstreamCarrierAsync(
                            false,
                            cancellationToken);
                        pickStep = PickStep.Pcb1;
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
            if (!handler.AtHandoffXY)
            {
                return buffer.CanSupplyEnter
                    ? PcbSupplyState.MovingAboveBuffer
                    : PcbSupplyState.WaitingForBuffer;
            }

            if (rotation != PcbSupplyRotationState.Rotated)
            {
                return PcbSupplyState.RotatingForBuffer;
            }

            return buffer.CanSupplyEnter
                ? PcbSupplyState.MovingToBuffer
                : PcbSupplyState.WaitingForBuffer;
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
