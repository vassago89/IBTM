using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.PcbBuffer;

namespace IBTM.PcbSupply;

public sealed class PcbSupplier : AutoUnit
{
    private readonly PcbSupplyHandler _handler;
    private readonly BufferStage _buffer;
    // Slot progress belongs only to the current run and upstream carrier.
    private PickStep _pickStep;

    public PcbSupplier(PcbSupplyHandler handler, BufferStage buffer)
    {
        _handler = handler;
        _buffer = buffer;
        _handler.Changed += OnHandlerChanged;
    }

    public override event Action? Changed
    {
        add
        {
            _handler.Changed += value;
            _buffer.StateChanged += value;
        }

        remove
        {
            _handler.Changed -= value;
            _buffer.StateChanged -= value;
        }
    }

    public async Task RunAsync(PcbSupplyRecipe recipe, CancellationToken cancellationToken = default)
    {
        Exception? failure = null;
        try
        {
            BeginRun();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await ExecuteAsync(recipe, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
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

    private async Task ExecuteAsync(PcbSupplyRecipe recipe, CancellationToken cancellationToken)
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
        if (state == PcbSupplyState.WaitingForCarrier && !_handler.IsAtPickupXY(recipe.Pcb1PickPosition))
            state = PcbSupplyState.MovingToPickup;
        TraceStep(state, _pickStep.ToString());
        switch (state)
        {
            case PcbSupplyState.MovingToPickup:
                await _handler.MoveToPickupAsync(recipe.Pcb1PickPosition, cancellationToken);
                break;
            case PcbSupplyState.PickingPcb:
                await PickPcbAsync(recipe, cancellationToken);
                break;
            case PcbSupplyState.SecuringPcb:
                await _handler.SetGripperClosedAsync(true, cancellationToken);
                await _handler.SetIpmFixerAsync(true, cancellationToken);
                await _handler.MoveToRotationZAsync(cancellationToken);
                break;
            case PcbSupplyState.RaisingForPickup:
                await _handler.MoveToRotationZAsync(cancellationToken);
                break;
            case PcbSupplyState.RotatingForHandoff:
                await _handler.SetRotatedAsync(true, cancellationToken);
                break;
            case PcbSupplyState.MovingToHandoff:
                await _handler.MoveToHandoffAsync(cancellationToken);
                break;
            case PcbSupplyState.ReleasingPcb:
                if (_handler.IpmFixed)
                {
                    if (!_buffer.IsPlacementSecuredAtHandoff())
                        throw new InvalidOperationException("Placement must detect and secure the PCB before supply releases its fixer.");
                    await _handler.SetIpmFixerAsync(false, cancellationToken);
                }
                if (_handler.Gripper != PcbSupplyCylinderState.Backward)
                {
                    if (!_buffer.IsPlacementSecuredAtHandoff())
                        throw new InvalidOperationException("Placement lost PCB holding feedback before supply opened its gripper.");
                    await _handler.SetGripperClosedAsync(false, cancellationToken);
                }
                break;
            case PcbSupplyState.MovingFromHandoff:
                await _handler.MoveFromHandoffAsync(
                    _pickStep == PickStep.Pcb2 ? recipe.Pcb2PickPosition : recipe.Pcb1PickPosition,
                    cancellationToken);
                break;
            case PcbSupplyState.UnrotatingForPickup:
                await _handler.SetRotatedAsync(false, cancellationToken);
                break;
            default:
                await WaitForChangeAsync(cancellationToken);
                break;
        }
    }

    private async Task PickPcbAsync(PcbSupplyRecipe recipe, CancellationToken cancellationToken)
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
    }

    private void OnHandlerChanged()
    {
        if (_pickStep != PickStep.Pcb1
            && !_handler.UpstreamCarrierAvailable)
        {
            _pickStep = PickStep.Pcb1;
        }
    }

    private PcbSupplyState State
    {
        get
        {
            var pcb = _handler.Pcb;
            var rotation = _handler.Rotation;

            switch (true)
            {
                case true when _buffer.IsSupplyAtHandoff() && _handler.PcbReleased:
                    return _buffer.IsSupplyExitAllowed
                        ? PcbSupplyState.MovingFromHandoff
                        : PcbSupplyState.WaitingForPlacementLift;
                case true when _buffer.IsSupplyAtHandoff():
                    return _buffer.IsPlacementSecuredAtHandoff()
                        ? PcbSupplyState.ReleasingPcb
                        : PcbSupplyState.WaitingForPlacement;
            }

            switch (true)
            {
                case true when pcb == PcbSupplyPcbState.Detected:
                    return PcbSupplyState.SecuringPcb;
                case true when pcb == PcbSupplyPcbState.Secured:
                    if (rotation != PcbSupplyRotationState.Rotated)
                    {
                        return PcbSupplyState.RotatingForHandoff;
                    }

                    return PcbSupplyState.MovingToHandoff;
                case true when !_handler.IsAtRotationZ():
                    return PcbSupplyState.RaisingForPickup;
                case true when rotation != PcbSupplyRotationState.Unrotated:
                    return PcbSupplyState.UnrotatingForPickup;
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
