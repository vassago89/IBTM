using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.PcbSupply;

public sealed class PcbSupplier : AutoUnit, IPcbSupplyHandoff
{
    private readonly PcbSupplyHandler _handler;
    private readonly UnitSettings _units;
    // Slot progress belongs only to the current run and upstream carrier.
    private PickStep _pickStep;

    public PcbSupplier(PcbSupplyHandler handler, UnitSettings units)
    {
        _handler = handler;
        _units = units;
        _handler.Changed += OnHandlerChanged;
    }

    public override event Action? Changed
    {
        add
        {
            _handler.Changed += value;
            _handler.Feedback.StateChanged += value;
        }

        remove
        {
            _handler.Changed -= value;
            _handler.Feedback.StateChanged -= value;
        }
    }

    public async Task RunAsync(
        PcbSupplyRecipe recipe,
        IPcbPlacementHandoff placement,
        CancellationToken cancellationToken = default)
    {
        Exception? failure = null;
        try
        {
            BeginRun();
            placement.Changed += OnChanged;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await ExecuteAsync(recipe, placement, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                placement.Changed -= OnChanged;
                EndRun(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _pickStep = PickStep.Pcb1;
            try
            {
                _handler.StopUpstream();
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
        }
    }

    private async Task ExecuteAsync(
        PcbSupplyRecipe recipe,
        IPcbPlacementHandoff placement,
        CancellationToken cancellationToken)
    {
        if (_pickStep != PickStep.WaitingForCarrierExit)
        {
            _handler.SetUpstreamReady(true);
        }
        else if (_handler.IsAtRotationZ() && _handler.Pcb != PcbSupplyPcbState.Detected)
        {
            // Keep Ready through both slot checks and the final pickup lift.
            // Its falling edge tells the upstream machine that pickup is complete.
            _handler.SetUpstreamReady(false);
        }

        var state = State;
        switch (state)
        {
            case PcbSupplyState.WaitingForPlacement when placement.Handoff == PcbPlacementHandoff.Holding:
                state = PcbSupplyState.ReleasingPcb;
                break;
            case PcbSupplyState.WaitingForPlacementLift when placement.Handoff == PcbPlacementHandoff.Clear:
                state = PcbSupplyState.MovingToPickup;
                break;
        }
        if (state == PcbSupplyState.WaitingForCarrier && !_handler.IsAtPickupXY(recipe.Pcb1PickPosition))
            state = PcbSupplyState.MovingToPickup;
        TraceStep(state, _pickStep.ToString());
        switch (state)
        {
            case PcbSupplyState.MovingToPickup:
                var nextPick = _pickStep == PickStep.Pcb2 ? recipe.Pcb2PickPosition : recipe.Pcb1PickPosition;
                if (_handler.IsAtHandoff())
                    await _handler.MoveFromHandoffAsync(nextPick, cancellationToken);
                await _handler.SetRotatedAsync(true, cancellationToken);
                if (!_handler.IsAtPickupXY(nextPick))
                    await _handler.MoveToPickupAsync(nextPick, cancellationToken);
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
                    if (!_handler.UpstreamCarrierAvailable)
                    {
                        carrierChanged = true;
                        pickup.Cancel();
                    }
                }

                _handler.Changed += StopWhenCarrierLeaves;
                try
                {
                    StopWhenCarrierLeaves();
                    await _handler.PickAsync(pickPosition, pickup.Token);
                    // Never advance a replacement carrier.
                    if (!carrierChanged)
                        _pickStep = pickStep == PickStep.Pcb1 ? PickStep.Pcb2 : PickStep.WaitingForCarrierExit;
                }
                catch (OperationCanceledException) when (carrierChanged
                    && !cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    _handler.Changed -= StopWhenCarrierLeaves;
                }
                break;
            }
            case PcbSupplyState.MovingToHandoff:
                if (_handler.Pcb == PcbSupplyPcbState.Detected)
                {
                    await _handler.SetGripperClosedAsync(true, cancellationToken);
                    await _handler.SetIpmFixerAsync(true, cancellationToken);
                    await _handler.MoveToRotationZAsync(cancellationToken);
                }
                if (_handler.Rotation != PcbSupplyRotationState.Unrotated)
                    await _handler.SetRotatedAsync(false, cancellationToken);
                await _handler.MoveToHandoffAsync(cancellationToken);
                break;
            case PcbSupplyState.ReleasingPcb:
                if (_handler.IpmFixed)
                {
                    if (placement.Handoff != PcbPlacementHandoff.Holding)
                        throw new InvalidOperationException("Placement must detect and secure the PCB before supply releases its fixer.");
                    await _handler.SetIpmFixerAsync(false, cancellationToken);
                }
                if (_handler.Gripper != PcbSupplyCylinderState.Backward)
                {
                    if (placement.Handoff != PcbPlacementHandoff.Holding)
                        throw new InvalidOperationException("Placement lost PCB holding feedback before supply opened its gripper.");
                    await _handler.SetGripperClosedAsync(false, cancellationToken);
                }
                break;
            default:
                await WaitForChangeAsync(cancellationToken);
                break;
        }
    }

    private void OnHandlerChanged()
    {
        if (_pickStep != PickStep.Pcb1
            && !_handler.UpstreamCarrierAvailable)
        {
            _pickStep = PickStep.Pcb1;
        }
    }

    public PcbSupplyHandoff Handoff
    {
        get
        {
            switch (State)
            {
                case PcbSupplyState.WaitingForPlacement:
                    return PcbSupplyHandoff.Holding;
                case PcbSupplyState.WaitingForPlacementLift:
                    return PcbSupplyHandoff.Released;
                default:
                    return PcbSupplyHandoff.Unavailable;
            }
        }
    }

    public PcbSupplyState State
    {
        get
        {
            if (!_units.PcbSupply)
                return PcbSupplyState.Disabled;
            var pcb = _handler.Pcb;
            var rotation = _handler.Rotation;
            var atHandoff = _handler.IsAtHandoff();

            switch (true)
            {
                case true when atHandoff && _handler.PcbReleased:
                    return PcbSupplyState.WaitingForPlacementLift;
                case true when atHandoff && pcb == PcbSupplyPcbState.Secured:
                    return PcbSupplyState.WaitingForPlacement;
                case true when atHandoff:
                    return PcbSupplyState.ReleasingPcb;
            }

            switch (true)
            {
                case true when pcb != PcbSupplyPcbState.None:
                    return PcbSupplyState.MovingToHandoff;
                case true when !_handler.IsAtRotationZ() || rotation != PcbSupplyRotationState.Rotated:
                    return PcbSupplyState.MovingToPickup;
                case true when _pickStep == PickStep.WaitingForCarrierExit:
                    return PcbSupplyState.WaitingForCarrierExit;
                default:
                    return _handler.UpstreamCarrierAvailable
                        ? PcbSupplyState.PickingPcb
                        : PcbSupplyState.WaitingForCarrier;
            }
        }
    }

    private enum PickStep
    {
        Pcb1,
        Pcb2,
        WaitingForCarrierExit,
    }
}
