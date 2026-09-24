using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed partial class PcbSupplier
{
    public async Task RunAsync(
        PcbSupplyRecipe recipe,
        IPcbPlacementHandoff placement,
        CancellationToken cancellationToken = default,
        bool repeat = false)
    {
        Exception? failure = null;
        _repeat = repeat;
        try
        {
            var initialStep = SequenceStep ?? PcbSupplyState.MovingToPickup;
            if (repeat && _units.PcbPlacement && State == PcbSupplyState.HandingOff
                && (!PcbSecured || placement.Handoff == PcbPlacementHandoff.Returning)
                && placement.Handoff != PcbPlacementHandoff.Holding)
                initialStep = PcbSupplyState.WaitingForReturnedPcb;
            BeginRun(initialStep);
            placement.Changed += OnChanged;
            while (!cancellationToken.IsCancellationRequested)
            {
                // A partial grip is valid only during pickup or an active handoff.
                if (Pcb == PcbSupplyPcbState.Detected && !PcbReleased
                    && State != PcbSupplyState.PickingPcb
                    && !(State == PcbSupplyState.HandingOff
                        && placement.Handoff is PcbPlacementHandoff.Holding or PcbPlacementHandoff.Returning))
                {
                    throw new InvalidOperationException(
                        "Supply PCB grip is incomplete away from a confirmed support. Check gripper and IPM fixation before moving or releasing it.");
                }
                var step = GetNextStep(placement, repeat);
                if (!await ExecuteStepAsync(step, recipe, placement, repeat, cancellationToken))
                    await WaitForChangeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            placement.Changed -= OnChanged;
            if (!repeat)
                _pickStep = PickStep.Pcb1;
            _repeat = false;
            try
            {
                StopUpstream();
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
            finally
            {
                EndRun(cancellationToken);
            }
        }
    }

    public PcbSupplyState GetNextStep(IPcbPlacementHandoff placement, bool repeat = false)
    {
        var state = State;
        if (repeat && _units.PcbSupply)
        {
            switch (state)
            {
                case PcbSupplyState.WaitingForReturnedPcb:
                    return placement.ReturningPcb is not null
                        ? PcbSupplyState.PreparingReturnReceipt : state;
                case PcbSupplyState.WaitingForReturnedPcbGrip:
                    return placement.Handoff == PcbPlacementHandoff.Returning
                        ? PcbSupplyState.ReceivingReturnedPcb : state;
                case PcbSupplyState.WaitingForReturnClear:
                    return placement.Handoff == PcbPlacementHandoff.Clear
                        ? PcbSupplyState.ReturningToPickup : state;
                case PcbSupplyState.PreparingReturnReceipt or PcbSupplyState.ReceivingReturnedPcb
                    or PcbSupplyState.ReturningToPickup:
                    return state;
                case PcbSupplyState.HandingOff:
                    if (!_units.PcbPlacement)
                        return PcbSupplyState.ReturningToPickup;
                    return state;
                case PcbSupplyState.WaitingForPlacementClear:
                    return placement.Handoff == PcbPlacementHandoff.Clear
                        ? PcbSupplyState.WaitingForReturnedPcb : state;
                default:
                    if (PcbSecured)
                        return PcbSupplyState.MovingToHandoff;
                    if (_units.PcbPlacement)
                        return placement.ReturningPcb is not null
                            ? PcbSupplyState.PreparingReturnReceipt : PcbSupplyState.WaitingForReturnedPcb;
                    break;
            }
        }
        switch (state)
        {
            case PcbSupplyState.WaitingForCarrier when UpstreamCarrierAvailable:
                return PcbSupplyState.PickingPcb;
            case PcbSupplyState.WaitingForPlacementClear when placement.Handoff == PcbPlacementHandoff.Clear:
            case PcbSupplyState.WaitingForCarrierExit when _pickStep == PickStep.Pcb1 || !UpstreamCarrierAvailable:
                return PcbSupplyState.MovingToPickup;
            default:
                return state;
        }
    }

    private async Task<bool> ExecuteStepAsync(
        PcbSupplyState step,
        PcbSupplyRecipe recipe,
        IPcbPlacementHandoff placement,
        bool repeat,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (step == PcbSupplyState.Disabled)
        {
            TraceStep(step);
            return false;
        }
        if (!repeat && State is PcbSupplyState.HandingOff or PcbSupplyState.WaitingForPlacementClear
            && Rotation != PcbSupplyRotationState.Unrotated)
            throw new MotionInterlockException("Supply handoff requires confirmed Unrotated feedback.");

        if ((!repeat || !_units.PcbPlacement) && _pickStep != PickStep.WaitingForCarrierExit)
        {
            SetUpstreamReady(true);
        }
        else if (State is PcbSupplyState.HandingOff or PcbSupplyState.WaitingForCarrierExit)
        {
            // Keep Ready through both slot checks and the final pickup lift.
            // Its falling edge tells the upstream machine that pickup is complete.
            SetUpstreamReady(false);
        }

        if (State == PcbSupplyState.WaitingForCarrierExit && step == PcbSupplyState.MovingToPickup)
            _pickStep = PickStep.Pcb1;
        var wasAtHandoff = _handoffPosition is { } handoffPosition
            && Motion.IsHoldingPosition(handoffPosition)
            && Rotation == PcbSupplyRotationState.Unrotated;
        EnterStep(step, _pickStep.ToString());
        switch (step)
        {
            case PcbSupplyState.PreparingReturnReceipt:
                if (placement.ReturningPcb is not { } returnedPcb)
                    return false;
                _pickStep = returnedPcb == HeatSinkSlot.HeatSink2 ? PickStep.Pcb2 : PickStep.Pcb1;
                if (Rotation != PcbSupplyRotationState.Unrotated
                    && placement.Handoff is PcbPlacementHandoff.Returning or PcbPlacementHandoff.Holding)
                    throw new MotionInterlockException("Supply cannot prepare rotation while Placement holds the PCB at receive Z.");
                if (!PcbSecured)
                {
                    await SetIpmFixerAsync(false, cancellationToken);
                    await SetGripperClosedAsync(false, cancellationToken);
                }
                if (!wasAtHandoff)
                {
                    await SetRotatedAsync(false, cancellationToken);
                    await PrepareHandoffAsync(cancellationToken);
                }
                State = PcbSupplyState.WaitingForReturnedPcbGrip;
                break;
            case PcbSupplyState.ReceivingReturnedPcb:
                if (placement.Handoff != PcbPlacementHandoff.Returning || Pcb == PcbSupplyPcbState.None)
                    throw new InvalidOperationException("Supply must detect the returned PCB supported by placement before gripping it.");
                await SetGripperClosedAsync(true, cancellationToken);
                await SetIpmFixerAsync(true, cancellationToken);
                State = PcbSupplyState.WaitingForReturnClear;
                break;
            case PcbSupplyState.WaitingForReturnClear:
                if (!PcbSecured)
                    throw new InvalidOperationException("Supply lost the returned PCB before placement cleared the handoff.");
                return false;
            case PcbSupplyState.ReturningToPickup:
            {
                // Keep the returned PCB gripped; Repeat never places it upstream.
                var returnPosition = _pickStep == PickStep.Pcb1 ? recipe.Pcb1PickPosition : recipe.Pcb2PickPosition;
                using var returning = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                void CheckReturnHolding()
                {
                    if (!PcbSecured)
                        returning.Cancel();
                }
                Changed += CheckReturnHolding;
                try
                {
                    CheckReturnHolding();
                    returning.Token.ThrowIfCancellationRequested();
                    await MoveFromHandoffAsync(returnPosition, returning.Token);
                    await SetRotatedAsync(true, returning.Token);
                    await MoveToPickupAsync(returnPosition, returning.Token);
                    _handoffPendingDeparture = false;
                    State = PcbSupplyState.MovingToHandoff;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Supply lost PCB holding feedback during reverse travel.");
                }
                finally
                {
                    Changed -= CheckReturnHolding;
                }
                break;
            }
            case PcbSupplyState.MovingToPickup:
                var nextPick = _pickStep == PickStep.Pcb2 ? recipe.Pcb2PickPosition : recipe.Pcb1PickPosition;
                if (_handoffPendingDeparture)
                {
                    await MoveFromHandoffAsync(nextPick, cancellationToken);
                    await SetRotatedAsync(true, cancellationToken);
                    _handoffPendingDeparture = false;
                    State = PcbSupplyState.WaitingForCarrier;
                }
                else
                {
                    await SetRotatedAsync(true, cancellationToken);
                    await MoveToPickupAsync(nextPick, cancellationToken);
                    State = PcbSupplyState.WaitingForCarrier;
                }
                if (_pickStep == PickStep.WaitingForCarrierExit)
                    State = PcbSupplyState.WaitingForCarrierExit;
                break;
            case PcbSupplyState.PickingPcb:
            {
                var pickStep = _pickStep;
                var pickPosition = pickStep == PickStep.Pcb1
                    ? recipe.Pcb1PickPosition
                    : recipe.Pcb2PickPosition;
                using var pickup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var carrierChanged = false;
                void StopWhenCarrierLeaves()
                {
                    if (!UpstreamCarrierAvailable)
                    {
                        carrierChanged = true;
                        pickup.Cancel();
                    }
                }

                Changed += StopWhenCarrierLeaves;
                try
                {
                    StopWhenCarrierLeaves();
                    EnterStep(PcbSupplyState.PickingPcb, pickStep.ToString());
                    await PickAsync(pickPosition, pickup.Token);
                    // Never advance a replacement carrier.
                    if (!carrierChanged)
                    {
                        if (repeat)
                        {
                            if (PcbSecured)
                                await MoveToRotationZAsync(pickup.Token);
                            else
                                _pickStep = pickStep == PickStep.Pcb1 ? PickStep.Pcb2 : PickStep.Pcb1;
                        }
                        else
                            _pickStep = pickStep == PickStep.Pcb1 ? PickStep.Pcb2 : PickStep.WaitingForCarrierExit;
                        if (!PcbSecured && _pickStep == PickStep.WaitingForCarrierExit)
                            State = PcbSupplyState.WaitingForCarrierExit;
                    }
                }
                catch (OperationCanceledException) when (carrierChanged
                    && !cancellationToken.IsCancellationRequested)
                {
                    if (repeat)
                        throw new InvalidOperationException("The upstream carrier left during the initial repeat pickup.");
                    State = PcbSupplyState.WaitingForCarrier;
                }
                finally
                {
                    Changed -= StopWhenCarrierLeaves;
                }
                break;
            }
            case PcbSupplyState.MovingToHandoff:
            {
                using var handoff = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                void CheckHolding()
                {
                    if (!PcbSecured)
                        handoff.Cancel();
                }
                Changed += CheckHolding;
                try
                {
                    CheckHolding();
                    handoff.Token.ThrowIfCancellationRequested();
                    if (Rotation != PcbSupplyRotationState.Unrotated)
                        await SetRotatedAsync(false, handoff.Token);
                    await PrepareHandoffAsync(handoff.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Supply lost PCB grip or IPM fixation during forward handoff.");
                }
                finally
                {
                    Changed -= CheckHolding;
                }
                break;
            }
            case PcbSupplyState.HandingOff:
                if (repeat && !PcbSecured && placement.Handoff != PcbPlacementHandoff.Holding)
                    throw new InvalidOperationException("Supply repeat lost PCB holding feedback during forward handoff.");
                if (PcbSecured && placement.Handoff != PcbPlacementHandoff.Holding)
                    return false;
                if (Rotation != PcbSupplyRotationState.Unrotated)
                    throw new MotionInterlockException("Supply must remain Unrotated while releasing the PCB.");
                if (IpmFixed)
                {
                    if (placement.Handoff != PcbPlacementHandoff.Holding)
                        throw new InvalidOperationException("Placement must detect and secure the PCB before supply releases its fixer.");
                    await SetIpmFixerAsync(false, cancellationToken);
                }
                if (Gripper != PcbSupplyCylinderState.Backward)
                {
                    if (Rotation != PcbSupplyRotationState.Unrotated)
                        throw new MotionInterlockException("Supply lost Unrotated feedback before opening its gripper.");
                    if (placement.Handoff != PcbPlacementHandoff.Holding)
                        throw new InvalidOperationException("Placement lost PCB holding feedback before supply opened its gripper.");
                    await SetGripperClosedAsync(false, cancellationToken);
                }
                State = PcbSupplyState.WaitingForPlacementClear;
                break;
            default:
                return false;
        }
        return true;
    }

    public async Task PrepareHandoffAsync(CancellationToken cancellationToken)
    {
        if (Rotation != PcbSupplyRotationState.Unrotated)
            throw new MotionInterlockException("Supply must be unrotated before moving to the handoff position.");
        var position = _settings.HandoffPosition;
        State = PcbSupplyState.MovingToHandoff;
        await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
        await _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _handoffPendingDeparture = true;
        _handoffPosition = new() { X = position.X, Y = position.Y, Z = position.Z };
        State = PcbSupplyState.HandingOff;
    }

    internal async Task PickAsync(
        PcbPickPosition position,
        CancellationToken cancellationToken = default)
    {
        await SetIpmFixerAsync(false, cancellationToken);
        await SetGripperClosedAsync(false, cancellationToken);
        State = PcbSupplyState.MovingToPickup;
        await MoveToPickupAsync(position, cancellationToken);
        await _motion.MoveAxisAsync(MotionAxis.Z, position.Z, _settings.Motion.ZSpeed, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        State = PcbSupplyState.PickingPcb;
        if (Pcb == PcbSupplyPcbState.None)
        {
            await MoveToRotationZAsync(cancellationToken);
            State = PcbSupplyState.WaitingForCarrier;
            return;
        }
        // Presence can be ON before reaching the PCB; grip only at the taught pickup XYZ.
        await SetGripperClosedAsync(true, cancellationToken);
        await SetIpmFixerAsync(true, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        State = PcbSupplyState.MovingToHandoff;
    }
}
