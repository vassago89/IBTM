using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed partial class NgShuttle : AutoUnit
{
    private readonly IIoService _io;
    private readonly NgCarrierConveyor _conveyor;
    private readonly INgCarrierTransferFeedback _transfer;

    public NgShuttle(
        IIoService io,
        NgCarrierConveyor conveyor,
        INgCarrierTransferFeedback transfer)
    {
        _io = io;
        _conveyor = conveyor;
        _transfer = transfer;
        io.InputChanged += OnInputChanged;
        conveyor.Changed += NotifyChanged;
        transfer.Changed += NotifyChanged;
    }

    public override event Action? Changed;

    public bool CarrierDetected => _io.GetInput(InputIo.NgShuttleCarrierDetected);

    public NgShuttleLiftState Lift
    {
        get
        {
            switch ((_io.GetInput(InputIo.NgShuttleUp), _io.GetInput(InputIo.NgShuttleDown)))
            {
                case (true, false):
                    return NgShuttleLiftState.Up;
                case (false, true):
                    return NgShuttleLiftState.Down;
                default:
                    return NgShuttleLiftState.Between;
            }
        }
    }

    public NgShuttleState State
    {
        get
        {
            switch (true)
            {
                case true when Lift == NgShuttleLiftState.Down:
                    switch (true)
                    {
                        case true when _conveyor.IsShuttleRaiseAllowed:
                            return NgShuttleState.Raising;
                        case true when _conveyor.Position3Occupied
                            || _conveyor.RunCommandOn:
                            return NgShuttleState.WaitingForConveyor;
                        default:
                            return NgShuttleState.CarrierPositionUnknown;
                    }
                case true when CarrierDetected:
                    if (!_transfer.IsRaised)
                    {
                        return NgShuttleState.WaitingForCarrierPickupUp;
                    }

                    return _conveyor.IsAcceptCarrierAllowed()
                        ? NgShuttleState.Lowering
                        : NgShuttleState.WaitingForConveyor;
                default:
                    return Lift == NgShuttleLiftState.Up
                        ? NgShuttleState.WaitingForCarrier
                        : NgShuttleState.Raising;
            }
        }
    }

    public bool IsReceiveAllowed(bool useConveyor, bool? conveyorRunning = null)
    {
        return Lift == NgShuttleLiftState.Up
            && !CarrierDetected
            && (!useConveyor || _conveyor.IsAcceptCarrierAllowed(conveyorRunning));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        BeginRun();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = State;
                TraceStep(state);
                switch (state)
                {
                    case NgShuttleState.Lowering:
                        await SetDownAsync(true, cancellationToken);
                        break;
                    case NgShuttleState.Raising:
                        await SetDownAsync(false, cancellationToken);
                        break;
                    default:
                        await WaitForChangeAsync(cancellationToken);
                        break;
                }
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

    public Task SetDownAsync(bool down, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, down, cancellationToken);
    }

    public Task WaitForCarrierAsync(CancellationToken cancellationToken = default)
    {
        return _io.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, true, cancellationToken);
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.NgShuttleUp or InputIo.NgShuttleDown or InputIo.NgShuttleCarrierDetected)
            Changed?.Invoke();
    }
}
