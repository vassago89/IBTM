using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgShuttle : AutoUnit
{
    private readonly IIoService _io;
    private readonly NgCarrierConveyor _conveyor;
    private readonly INgCarrierTransferFeedback _transfer;

    public NgShuttle(
        IIoService io,
        NgCarrierConveyor conveyor,
        NgShuttleFeedback feedback,
        INgCarrierTransferFeedback transfer)
    {
        _io = io;
        _conveyor = conveyor;
        _transfer = transfer;
        Feedback = feedback;
        feedback.Changed += NotifyChanged;
        conveyor.Changed += NotifyChanged;
        transfer.Changed += NotifyChanged;
    }

    public override event Action? Changed;

    public NgShuttleFeedback Feedback { get; }

    public bool CanReceive(bool useConveyor, bool? conveyorRunning = null)
    {
        return Feedback.Lift == NgShuttleLiftState.Up
            && !Feedback.CarrierDetected
            && (!useConveyor || _conveyor.CanAcceptCarrier(conveyorRunning));
    }

    public NgShuttleState State
    {
        get
        {
            if (Feedback.Lift == NgShuttleLiftState.Down)
            {
                if (_conveyor.ShuttleCanRaise)
                {
                    return NgShuttleState.Raising;
                }

                if (_conveyor.Position3Occupied
                    || _conveyor.RunCommandOn)
                {
                    return NgShuttleState.WaitingForConveyor;
                }

                return NgShuttleState.CarrierPositionUnknown;
            }

            if (Feedback.CarrierDetected)
            {
                if (!_transfer.IsRaised)
                {
                    return NgShuttleState.WaitingForCarrierPickupUp;
                }

                return _conveyor.CanAcceptCarrier()
                    ? NgShuttleState.Lowering
                    : NgShuttleState.WaitingForConveyor;
            }

            return Feedback.Lift == NgShuttleLiftState.Up
                ? NgShuttleState.WaitingForCarrier
                : NgShuttleState.Raising;
        }
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return RunLoopAsync(ExecuteAsync, cancellationToken);
    }

    private Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var state = State;
        TraceStep(state);
        return state switch
        {
            NgShuttleState.Lowering => SetDownAsync(true, cancellationToken),
            NgShuttleState.Raising => SetDownAsync(false, cancellationToken),
            _ => WaitForChangeAsync(cancellationToken),
        };
    }

    public Task SetDownAsync(bool down, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, down, cancellationToken);
    }

    public async Task CycleAsync(CancellationToken cancellationToken)
    {
        if (!Feedback.CarrierDetected || !_transfer.IsRaised)
        {
            throw new InvalidOperationException("Shuttle repeat requires a carrier on the shuttle and the NG pickup raised.");
        }

        await SetDownAsync(true, cancellationToken);

        if (!Feedback.CarrierDetected || !_transfer.IsRaised)
        {
            throw new InvalidOperationException("Shuttle repeat lost its carrier or raised pickup feedback before ascent.");
        }

        await SetDownAsync(false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task WaitForCarrierAsync(CancellationToken cancellationToken = default)
    {
        return _io.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, true, cancellationToken);
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }
}
