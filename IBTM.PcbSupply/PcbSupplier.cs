using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.PcbBuffer;

namespace IBTM.PcbSupply;

public sealed class PcbSupplier(
    PcbSupplyHandler handler,
    BufferStage buffer) : AutoUnit
{
    public bool CanHome =>
        handler.CanPrepareHome
        && (handler.Rotation != PcbSupplyRotationState.Unrotated
            || !buffer.PcbPresent);

    public override event Action? Changed
    {
        add
        {
            handler.Changed += value;
            buffer.StateChanged += value;
        }
        remove
        {
            handler.Changed -= value;
            buffer.StateChanged -= value;
        }
    }

    public async Task RunAsync(
        PcbSupplyRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        var pickStep = PickStep.Pcb1;
        void OnStateChanged()
        {
            if (pickStep == PickStep.WaitingForCarrierExit
                && !handler.UpstreamCarrierAvailable)
            {
                pickStep = PickStep.Pcb1;
            }
        }

        async Task PickAsync(CancellationToken token)
        {
            await handler.PickAsync(
                pickStep == PickStep.Pcb1
                    ? recipe.Pcb1PickPosition
                    : recipe.Pcb2PickPosition,
                token);
            pickStep = pickStep == PickStep.Pcb1
                ? PickStep.Pcb2
                : PickStep.WaitingForCarrierExit;
        }

        Task ExecuteAsync(CancellationToken token)
        {
            if (pickStep != PickStep.WaitingForCarrierExit)
            {
                handler.SetUpstreamReady(
                    pickStep == PickStep.Pcb1 && !handler.UpstreamCarrierAvailable);
            }
            else if (handler.IsAtRotationZ
                     && handler.Pcb != PcbSupplyPcbState.Detected)
            {
                handler.SetUpstreamReady(true);
            }

            return State(pickStep) switch
            {
                PcbSupplyState.PickingPcb => PickAsync(token),
                PcbSupplyState.SecuringPcb => handler.SecurePcbAsync(token),
                PcbSupplyState.RaisingForPickup => handler.MoveToRotationZAsync(token),
                PcbSupplyState.MovingAboveBuffer => handler.MoveAboveHandoffAsync(token),
                PcbSupplyState.RotatingForBuffer => handler.SetRotatedAsync(true, token),
                PcbSupplyState.MovingToBuffer => handler.LowerToHandoffAsync(token),
                PcbSupplyState.WaitingForBufferPcb => buffer.WaitForPcbAsync(token),
                PcbSupplyState.ReleasingPcb => ReleasePcbAsync(token),
                PcbSupplyState.UnrotatingForPickup => handler.SetRotatedAsync(false, token),
                _ => WaitForChangeAsync(token),
            };
        }

        Changed += OnStateChanged;
        try
        {
            await RunLoopAsync(ExecuteAsync, cancellationToken);
        }
        finally
        {
            Changed -= OnStateChanged;
            handler.SetUpstreamReady(false);
        }
    }

    private async Task ReleasePcbAsync(CancellationToken cancellationToken)
    {
        await handler.SetIpmFixerAsync(false, cancellationToken);
        await handler.SetNestAsync(false, cancellationToken);
        await handler.MoveClearAsync(cancellationToken);
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
