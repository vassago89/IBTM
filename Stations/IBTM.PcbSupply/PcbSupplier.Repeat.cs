using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed partial class PcbSupplier
{
    // Current command only. START chooses forward handoff from live PCB feedback.
    private PcbSupplyState? _repeatState;

    private PcbSupplyState RepeatState
    {
        set
        {
            _repeatState = value;
            TraceStep(value, _pickStep.ToString());
            Changed?.Invoke();
        }
    }

    private async Task ExecuteRepeatAsync(
        PcbSupplyRecipe recipe,
        IPcbPlacementHandoff placement,
        CancellationToken cancellationToken)
    {
        // An interrupted release at handoff continues through ReleasePcbAsync below.
        // Do not reopen the gripper through reverse-receipt preparation.
        if (_units.PcbPlacement
            && (!PcbSecured || placement.Handoff == PcbPlacementHandoff.Returning)
            && !(IsAtHandoff() && placement.Handoff == PcbPlacementHandoff.Holding))
        {
            RepeatState = PcbSupplyState.HandingOff;
            while (placement.ReturningPcb is null && placement.Handoff != PcbPlacementHandoff.Holding)
                await WaitForChangeAsync(cancellationToken);

            if (placement.ReturningPcb is { } heatSink)
                _pickStep = heatSink == HeatSinkSlot.HeatSink2 ? PickStep.Pcb2 : PickStep.Pcb1;
            if (Rotation != PcbSupplyRotationState.Unrotated
                && placement.Handoff is PcbPlacementHandoff.Returning or PcbPlacementHandoff.Holding)
                throw new MotionInterlockException("Supply cannot prepare rotation while Placement holds the PCB at receive Z.");
            RepeatState = PcbSupplyState.MovingToHandoff;
            if (!PcbSecured)
            {
                await SetIpmFixerAsync(false, cancellationToken);
                await SetGripperClosedAsync(false, cancellationToken);
            }
            if (!IsAtHandoff() || Rotation != PcbSupplyRotationState.Unrotated)
            {
                await SetRotatedAsync(false, cancellationToken);
                await MoveToHandoffAsync(cancellationToken);
            }
            RepeatState = PcbSupplyState.HandingOff;
            while (placement.Handoff is not (PcbPlacementHandoff.Returning or PcbPlacementHandoff.Holding))
                await WaitForChangeAsync(cancellationToken);

            if (placement.Handoff == PcbPlacementHandoff.Returning)
            {
                if (Pcb == PcbSupplyPcbState.None)
                    throw new InvalidOperationException("Supply must detect the returned PCB before gripping it.");
                await SetGripperClosedAsync(true, cancellationToken);
                await SetIpmFixerAsync(true, cancellationToken);
                // Retain the PCB throughout the reverse travel; there is no upstream placement.
                await ReturnToPickupAsync(
                    _pickStep == PickStep.Pcb1 ? recipe.Pcb1PickPosition : recipe.Pcb2PickPosition,
                    placement, cancellationToken);
            }
        }
        else if (!_units.PcbPlacement && !PcbSecured)
        {
            RepeatState = PcbSupplyState.WaitingForCarrier;
            SetUpstreamReady(true);
            while (!UpstreamCarrierAvailable)
                await WaitForChangeAsync(cancellationToken);

            // Upstream presence is needed only for the initial pickup.
            using var pickup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            void CheckCarrier()
            {
                if (!UpstreamCarrierAvailable)
                    pickup.Cancel();
            }
            Changed += CheckCarrier;
            try
            {
                CheckCarrier();
                pickup.Token.ThrowIfCancellationRequested();
                if (Pcb == PcbSupplyPcbState.Detected && IsAtPickup(recipe.Pcb2PickPosition))
                    _pickStep = PickStep.Pcb2;
                var position = _pickStep == PickStep.Pcb1 ? recipe.Pcb1PickPosition : recipe.Pcb2PickPosition;
                RepeatState = PcbSupplyState.MovingToPickup;
                if (!IsAtPickup(position) || Rotation != PcbSupplyRotationState.Rotated)
                {
                    if (IsAtHandoff())
                        await MoveFromHandoffAsync(position, pickup.Token);
                    await SetRotatedAsync(true, pickup.Token);
                }
                RepeatState = PcbSupplyState.PickingPcb;
                await PickAsync(position, pickup.Token);
                if (Pcb == PcbSupplyPcbState.None)
                {
                    _pickStep = _pickStep == PickStep.Pcb1 ? PickStep.Pcb2 : PickStep.Pcb1;
                    return;
                }
                await MoveToRotationZAsync(pickup.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("The upstream carrier left during the initial repeat pickup.");
            }
            finally
            {
                Changed -= CheckCarrier;
            }
        }

        // Continue the forward handoff from current position and recipient holding.
        if (!_units.PcbPlacement || !IsAtHandoff()
            || placement.Handoff != PcbPlacementHandoff.Holding)
        {
            if (!PcbSecured)
                throw new InvalidOperationException("Supply forward handoff requires confirmed PCB grip and IPM fixation.");
            using var forward = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            void CheckHolding()
            {
                if (!PcbSecured)
                    forward.Cancel();
            }
            Changed += CheckHolding;
            try
            {
                CheckHolding();
                forward.Token.ThrowIfCancellationRequested();
                RepeatState = PcbSupplyState.MovingToHandoff;
                if (Rotation != PcbSupplyRotationState.Unrotated)
                    await SetRotatedAsync(false, forward.Token);
                await MoveToHandoffAsync(forward.Token);
                if (_units.PcbPlacement)
                {
                    RepeatState = PcbSupplyState.HandingOff;
                    while (placement.Handoff != PcbPlacementHandoff.Holding)
                        await WaitForChangeAsync(forward.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Supply repeat lost PCB holding feedback during forward handoff.");
            }
            finally
            {
                Changed -= CheckHolding;
            }
        }

        if (_units.PcbPlacement)
        {
            RepeatState = PcbSupplyState.HandingOff;
            await ReleasePcbAsync(placement, cancellationToken);
            RepeatState = PcbSupplyState.WaitingForPlacementZ;
            while (placement.Handoff != PcbPlacementHandoff.Clear)
                await WaitForChangeAsync(cancellationToken);
        }
        else
        {
            await ReturnToPickupAsync(
                _pickStep == PickStep.Pcb1 ? recipe.Pcb1PickPosition : recipe.Pcb2PickPosition,
                placement, cancellationToken);
        }
    }

    private async Task ReturnToPickupAsync(
        PcbPickPosition position,
        IPcbPlacementHandoff placement,
        CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckHolding()
        {
            if (!PcbSecured)
                operation.Cancel();
        }
        Changed += CheckHolding;
        try
        {
            CheckHolding();
            operation.Token.ThrowIfCancellationRequested();
            if (_units.PcbPlacement)
            {
                RepeatState = PcbSupplyState.WaitingForPlacementZ;
                while (placement.Handoff != PcbPlacementHandoff.Clear)
                    await WaitForChangeAsync(operation.Token);
            }
            RepeatState = PcbSupplyState.MovingToPickup;
            await MoveFromHandoffAsync(position, operation.Token);
            await SetRotatedAsync(true, operation.Token);
            await MoveToPickupAsync(position, operation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Supply lost PCB holding feedback during reverse travel.");
        }
        finally
        {
            Changed -= CheckHolding;
        }
    }
}
