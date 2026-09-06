using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgShuttle
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

    public event Action? Changed;

    public NgShuttleFeedback Feedback { get; }

    public bool CanReceive =>
        Feedback.Lift == NgShuttleLiftState.Up
        && !Feedback.CarrierDetected
        && _conveyor.CanAcceptCarrier;

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
                    || _conveyor.CarrierMoving
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

                if (_conveyor.Position3Occupied)
                {
                    return NgShuttleState.WaitingForPosition3;
                }

                return _conveyor.CanAcceptCarrier
                    ? NgShuttleState.Lowering
                    : NgShuttleState.WaitingForConveyor;
            }

            return Feedback.Lift == NgShuttleLiftState.Up
                ? NgShuttleState.WaitingForCarrier
                : NgShuttleState.Raising;
        }
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        Changed += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                switch (State)
                {
                    case NgShuttleState.Lowering:
                        await SetDownAsync(true, cancellationToken);
                        await _conveyor.WaitForPosition3Async(
                            cancellationToken);
                        break;
                    case NgShuttleState.Raising:
                        await SetDownAsync(false, cancellationToken);
                        break;
                    default:
                        await stateChanged.WaitAsync(cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Changed -= OnStateChanged;
        }
    }

    private Task SetDownAsync(
        bool down,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.NgShuttleDown,
            down,
            cancellationToken);

    public Task WaitForCarrierAsync(
        bool detected,
        CancellationToken cancellationToken = default) =>
        _io.WaitForInputAsync(
            InputIo.NgShuttleCarrierDetected,
            detected,
            cancellationToken);

    private void NotifyChanged() => Changed?.Invoke();
}
