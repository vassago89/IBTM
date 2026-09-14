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
    // Checked slots belong to the upstream carrier and survive STOP.
    private PickStep _pickStep;

    public PcbSupplier(PcbSupplyHandler handler, BufferStage buffer)
    {
        _handler = handler;
        _buffer = buffer;
        _handler.Changed += OnHandlerChanged;
    }

    public bool CanHome
    {
        get
        {
            return _handler.CanPrepareHome;
        }
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
        async Task ExecuteAsync(CancellationToken token)
        {
            if (_pickStep != PickStep.WaitingForCarrierExit)
            {
                _handler.SetUpstreamReady(_pickStep == PickStep.Pcb1 && !_handler.UpstreamCarrierAvailable);
            }
            else if (_handler.IsAtRotationZ() && _handler.Pcb != PcbSupplyPcbState.Detected)
            {
                _handler.SetUpstreamReady(true);
            }

            var state = State();
            TraceStep(state, _pickStep.ToString());
            switch (state)
            {
                case PcbSupplyState.PickingPcb:
                    await PickPcbAsync(recipe, token);
                    break;
                case PcbSupplyState.SecuringPcb:
                    await _handler.SetGripperClosedAsync(true, token);
                    await _handler.SetIpmFixerAsync(true, token);
                    await _handler.MoveToRotationZAsync(token);
                    break;
                case PcbSupplyState.RaisingForPickup:
                    await _handler.MoveToRotationZAsync(token);
                    break;
                case PcbSupplyState.RotatingForHandoff:
                    await _handler.SetRotatedAsync(true, token);
                    break;
                case PcbSupplyState.MovingToHandoff:
                    await _handler.MoveToHandoffAsync(token);
                    break;
                case PcbSupplyState.ReleasingPcb:
                    if (_handler.IpmFixer != PcbSupplyCylinderState.Backward)
                    {
                        if (!_buffer.IsPlacementSecuredAtHandoff())
                            throw new InvalidOperationException("Placement must detect and secure the PCB before supply releases its fixer.");
                        await _handler.SetIpmFixerAsync(false, token);
                    }
                    if (_handler.Gripper != PcbSupplyCylinderState.Backward)
                    {
                        if (!_buffer.IsPlacementSecuredAtHandoff())
                            throw new InvalidOperationException("Placement lost PCB holding feedback before supply opened its gripper.");
                        await _handler.SetGripperClosedAsync(false, token);
                    }
                    await _handler.MoveClearAsync(token);
                    break;
                case PcbSupplyState.UnrotatingForPickup:
                    await _handler.SetRotatedAsync(false, token);
                    break;
                default:
                    await WaitForChangeAsync(token);
                    break;
            }
        }

        Exception? failure = null;
        try
        {
            await RunLoopAsync(ExecuteAsync, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                _handler.SetUpstreamReady(false);
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
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
            // A completed check survives STOP, but never advances a replacement carrier.
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

    private PcbSupplyState State()
    {
        var pcb = _handler.Pcb;
        var rotation = _handler.Rotation;

        if (_buffer.IsSupplyAtHandoff())
        {
            return _buffer.IsPlacementSecuredAtHandoff()
                || _handler.Gripper == PcbSupplyCylinderState.Backward
                    && _handler.IpmFixer == PcbSupplyCylinderState.Backward
                ? PcbSupplyState.ReleasingPcb
                : PcbSupplyState.WaitingForPlacement;
        }

        if (_buffer.IsSupplyInside() && pcb != PcbSupplyPcbState.Secured)
        {
            if (_handler.Gripper == PcbSupplyCylinderState.Backward
                && _handler.IpmFixer == PcbSupplyCylinderState.Backward)
                return PcbSupplyState.ReleasingPcb;
            throw new InvalidOperationException("Supply PCB holding feedback was lost inside the handoff zone. Check both handlers before resuming.");
        }

        if (pcb == PcbSupplyPcbState.Detected)
        {
            return PcbSupplyState.SecuringPcb;
        }

        if (pcb == PcbSupplyPcbState.Secured)
        {
            if (rotation != PcbSupplyRotationState.Rotated)
            {
                return PcbSupplyState.RotatingForHandoff;
            }

            return _buffer.CanEnterSupply()
                ? PcbSupplyState.MovingToHandoff
                : PcbSupplyState.WaitingForHandoff;
        }

        if (!_handler.IsAtRotationZ())
        {
            return PcbSupplyState.RaisingForPickup;
        }

        if (rotation != PcbSupplyRotationState.Unrotated)
        {
            return PcbSupplyState.UnrotatingForPickup;
        }

        if (_pickStep == PickStep.WaitingForCarrierExit)
        {
            return PcbSupplyState.WaitingForCarrierExit;
        }

        return _handler.UpstreamCarrierAvailable
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
