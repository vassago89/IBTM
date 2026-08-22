using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;
using IBTM.PcbBuffer;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyProcess
{
    private readonly PcbSupplyHandler _handler;
    private readonly BufferStage _buffer;
    private readonly OperationCancellation _operations;

    public PcbSupplyProcess(
        PcbSupplyHandler handler,
        BufferStage buffer,
        OperationCancellation operations)
    {
        _handler = handler;
        _buffer = buffer;
        _operations = operations;
        handler.Changed += NotifyChanged;
    }

    public event Action? Changed;

    public bool ReadyForHandoff =>
        _handler.PcbSecured && _buffer.SupplyAtHandoff;

    public bool CanHome =>
        _handler.CanPrepareHome
        && (_handler.Rotation != PcbSupplyRotation.Unrotated
            || !_buffer.PcbPresent);

    public bool CanMoveToBuffer =>
        !_handler.IsMoving
        && _handler.Rotation == PcbSupplyRotation.Rotated
        && _handler.PcbSecured
        && !_buffer.SupplyInside
        && _buffer.CanSupplyEnter;

    public void SetReady(bool ready) => _handler.SetReady(ready);

    public async Task MoveToBufferAsync(
        CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Link(cancellationToken);
        try
        {
            await MoveToBufferCoreAsync(operation.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task ReleaseToPlacementAsync(
        CancellationToken cancellationToken = default)
    {
        await _handler.SetIpmFixerAsync(false, cancellationToken);
        await _handler.SetNestAsync(false, cancellationToken);
        await _handler.MoveClearAsync(cancellationToken);
    }

    public async Task RunAsync(
        PcbSupplyRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        var stateChanged = new SemaphoreSlim(0);
        var slot = PcbCarrierSlot.Pcb1;
        var carrier = _handler.PcbSecured || _handler.CarrierAvailable
            ? PcbCarrierState.ProcessingCarrier
            : PcbCarrierState.WaitingForCarrier;

        void OnStateChanged() => stateChanged.Release();

        _handler.Changed += OnStateChanged;
        _buffer.StateChanged += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_handler.Rotation == PcbSupplyRotation.Between)
                {
                    await stateChanged.WaitAsync(cancellationToken);
                    continue;
                }

                if (_handler.PcbSecured)
                {
                    if (_handler.Rotation == PcbSupplyRotation.Unrotated)
                    {
                        await _handler.SetRotatedAsync(
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

                if (_handler.Rotation == PcbSupplyRotation.Rotated)
                {
                    if (_buffer.SupplyInside)
                    {
                        await stateChanged.WaitAsync(cancellationToken);
                    }
                    else
                    {
                        await _handler.SetRotatedAsync(
                            false,
                            cancellationToken);
                        if (carrier == PcbCarrierState.ProcessingCarrier
                            && slot == PcbCarrierSlot.Pcb1)
                        {
                            slot = PcbCarrierSlot.Pcb2;
                        }
                    }

                    continue;
                }

                if (carrier == PcbCarrierState.ReleasingCarrier)
                {
                    if (_handler.CarrierAvailable)
                    {
                        await stateChanged.WaitAsync(cancellationToken);
                        continue;
                    }

                    carrier = PcbCarrierState.WaitingForCarrier;
                }

                if (carrier == PcbCarrierState.WaitingForCarrier)
                {
                    SetReady(true);
                    if (!_handler.CarrierAvailable)
                    {
                        await stateChanged.WaitAsync(cancellationToken);
                        continue;
                    }

                    SetReady(false);
                    carrier = PcbCarrierState.ProcessingCarrier;
                    slot = PcbCarrierSlot.Pcb1;
                    continue;
                }

                var pcbDetected = await _handler.PickAsync(
                    slot == PcbCarrierSlot.Pcb1
                        ? recipe.Pcb1PickPosition
                        : recipe.Pcb2PickPosition,
                    cancellationToken);
                if (slot == PcbCarrierSlot.Pcb1)
                {
                    if (!pcbDetected)
                    {
                        slot = PcbCarrierSlot.Pcb2;
                    }

                    continue;
                }

                carrier = _handler.CarrierAvailable
                    ? PcbCarrierState.ReleasingCarrier
                    : PcbCarrierState.WaitingForCarrier;
                SetReady(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _handler.Changed -= OnStateChanged;
            _buffer.StateChanged -= OnStateChanged;
            SetReady(false);
        }
    }

    private async Task MoveToBufferCoreAsync(
        CancellationToken cancellationToken)
    {
        await _handler.MoveToHandoffAsync(cancellationToken);
        await _buffer.WaitForPcbAsync(true, cancellationToken);
    }

    private void NotifyChanged() => Changed?.Invoke();
}
