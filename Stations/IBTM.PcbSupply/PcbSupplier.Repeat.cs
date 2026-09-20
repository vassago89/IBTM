using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.PcbSupply;

public sealed partial class PcbSupplier
{
    // Current command only. START chooses forward handoff from live PCB feedback.
    private PcbSupplyState? _repeatState;
    private event Action? RepeatChanged;

    private PcbSupplyState RepeatState
    {
        set
        {
            _repeatState = value;
            TraceStep(value, _pickStep.ToString());
            RepeatChanged?.Invoke();
        }
    }

    private async Task ExecuteRepeatAsync(
        PcbSupplyRecipe recipe,
        IPcbPlacementHandoff placement,
        CancellationToken cancellationToken)
    {
        if (_units.PcbPlacement && (_handler.Pcb == PcbSupplyPcbState.None
            || placement.Handoff == PcbPlacementHandoff.Returning))
        {
            RepeatState = PcbSupplyState.WaitingForPlacement;
            while (placement.ReturningPcb is null && placement.Handoff != PcbPlacementHandoff.Holding)
                await WaitForChangeAsync(cancellationToken);

            if (placement.ReturningPcb is { } heatSink)
                _pickStep = heatSink == HeatSinkSlot.HeatSink2 ? PickStep.Pcb2 : PickStep.Pcb1;
            RepeatState = PcbSupplyState.MovingToHandoff;
            if (_handler.Pcb == PcbSupplyPcbState.None)
            {
                await _handler.SetIpmFixerAsync(false, cancellationToken);
                await _handler.SetGripperClosedAsync(false, cancellationToken);
            }
            if (!_handler.IsAtHandoff())
            {
                await _handler.SetRotatedAsync(false, cancellationToken);
                await _handler.MoveToHandoffAsync(cancellationToken);
            }
            RepeatState = PcbSupplyState.WaitingForPlacement;
            while (placement.Handoff is not (PcbPlacementHandoff.Returning or PcbPlacementHandoff.Holding))
                await WaitForChangeAsync(cancellationToken);

            if (placement.Handoff == PcbPlacementHandoff.Returning)
            {
                if (_handler.Pcb == PcbSupplyPcbState.None)
                    throw new InvalidOperationException("Supply must detect the returned PCB before gripping it.");
                await _handler.SetGripperClosedAsync(true, cancellationToken);
                await _handler.SetIpmFixerAsync(true, cancellationToken);
                // Retain the PCB throughout the reverse travel; there is no upstream placement.
                await ReturnToPickupAsync(
                    _pickStep == PickStep.Pcb1 ? recipe.Pcb1PickPosition : recipe.Pcb2PickPosition,
                    placement, cancellationToken);
            }
        }
        else if (!_units.PcbPlacement && _handler.Pcb == PcbSupplyPcbState.None)
        {
            RepeatState = PcbSupplyState.WaitingForCarrier;
            _handler.SetUpstreamReady(true);
            while (!_handler.UpstreamCarrierAvailable)
                await WaitForChangeAsync(cancellationToken);

            // Upstream presence is needed only for the initial pickup.
            using var pickup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            void CheckCarrier()
            {
                if (!_handler.UpstreamCarrierAvailable)
                    pickup.Cancel();
            }
            _handler.Changed += CheckCarrier;
            try
            {
                CheckCarrier();
                pickup.Token.ThrowIfCancellationRequested();
                var position = _pickStep == PickStep.Pcb1 ? recipe.Pcb1PickPosition : recipe.Pcb2PickPosition;
                RepeatState = PcbSupplyState.MovingToPickup;
                if (_handler.IsAtHandoff())
                    await _handler.MoveFromHandoffAsync(position, pickup.Token);
                await _handler.SetRotatedAsync(true, pickup.Token);
                RepeatState = PcbSupplyState.PickingPcb;
                await _handler.PickAsync(position, pickup.Token);
                if (_handler.Pcb == PcbSupplyPcbState.None)
                {
                    _pickStep = _pickStep == PickStep.Pcb1 ? PickStep.Pcb2 : PickStep.Pcb1;
                    return;
                }
                await _handler.SetGripperClosedAsync(true, pickup.Token);
                await _handler.SetIpmFixerAsync(true, pickup.Token);
                await _handler.MoveToRotationZAsync(pickup.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("The upstream carrier left during the initial repeat pickup.");
            }
            finally
            {
                _handler.Changed -= CheckCarrier;
            }
        }

        // Continue the forward handoff from current position and recipient holding.
        if (!_units.PcbPlacement || !_handler.IsAtHandoff()
            || placement.Handoff != PcbPlacementHandoff.Holding)
        {
            if (_handler.Pcb == PcbSupplyPcbState.None)
                throw new InvalidOperationException("Supply forward handoff requires a detected PCB.");
            if (!_handler.PcbSecured)
            {
                await _handler.SetGripperClosedAsync(true, cancellationToken);
                await _handler.SetIpmFixerAsync(true, cancellationToken);
            }
            using var forward = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            void CheckHolding()
            {
                if (!_handler.PcbSecured)
                    forward.Cancel();
            }
            _handler.Changed += CheckHolding;
            try
            {
                CheckHolding();
                forward.Token.ThrowIfCancellationRequested();
                RepeatState = PcbSupplyState.MovingToHandoff;
                if (_handler.Rotation != PcbSupplyRotationState.Unrotated)
                    await _handler.SetRotatedAsync(false, forward.Token);
                await _handler.MoveToHandoffAsync(forward.Token);
                if (_units.PcbPlacement)
                {
                    RepeatState = PcbSupplyState.WaitingForPlacement;
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
                _handler.Changed -= CheckHolding;
            }
        }

        if (_units.PcbPlacement)
        {
            RepeatState = PcbSupplyState.ReleasingPcb;
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
            if (!_handler.PcbSecured)
                operation.Cancel();
        }
        _handler.Changed += CheckHolding;
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
            await _handler.MoveFromHandoffAsync(position, operation.Token);
            await _handler.SetRotatedAsync(true, operation.Token);
            await _handler.MoveToPickupAsync(position, operation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Supply lost PCB holding feedback during reverse travel.");
        }
        finally
        {
            _handler.Changed -= CheckHolding;
        }
    }
}
