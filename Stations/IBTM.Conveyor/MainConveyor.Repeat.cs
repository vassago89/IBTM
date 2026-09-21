using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Conveyor;

public sealed partial class MainConveyor
{
    private StationWork RepeatEndWork
    {
        get
        {
            if (_units.Inspection || _units.NgCarrierTransfer
                || _inspectionWork.Station.CarrierPresent
                || !_units.BoltFastening && !_units.PcbPlacement)
                return _inspectionWork;
            return _units.BoltFastening || _boltFasteningWork.Station.CarrierPresent
                ? _boltFasteningWork : _placementWork;
        }
    }

    public async Task WaitForRepeatEndAsync(CancellationToken cancellationToken)
    {
        var changed = new AsyncAutoResetEvent();
        Changed += changed.Set;
        try
        {
            while (RunCommandOn || _executingTransfer != MainConveyorState.Idle
                || !RepeatEndWork.Completed || !RepeatEndWork.Station.CarrierSeated)
                await changed.WaitAsync(cancellationToken);
        }
        finally
        {
            Changed -= changed.Set;
        }
    }

    public async Task ReturnToStartAsync(CancellationToken cancellationToken)
    {
        _repeat = true;
        try
        {
            Stop();
            using var runCancellation = _operations.Link(cancellationToken);
            _runCancellation = runCancellation;
            runCancellation.Disposed += () =>
            {
                if (ReferenceEquals(_runCancellation, runCancellation))
                    _runCancellation = null;
            };
            cancellationToken = runCancellation.Token;
            using var entryStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var motor = new ConveyorRun(_io, OutputIo.MainConveyorRun, entryStop.Token, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void StopAtEntry(InputIo input, bool value)
            {
                if (input != InputIo.MainConveyorEntryCarrierDetected || !value)
                    return;
                // Cancellation synchronously turns RUN off, before waking this sequence.
                // It also prevents motor setup from turning RUN on after an early arrival.
                try
                {
                    entryStop.Cancel();
                    arrived.TrySetResult();
                }
                catch (Exception exception)
                {
                    arrived.TrySetException(exception);
                }
            }

            _io.InputChanged += StopAtEntry;
            try
            {
                await Task.WhenAll(
                    _placementWork.Station.ReleaseAsync(cancellationToken),
                    _boltFasteningWork.Station.ReleaseAsync(cancellationToken),
                    _inspectionWork.Station.ReleaseAsync(cancellationToken));

                if (EntryCarrierDetected)
                    StopAtEntry(InputIo.MainConveyorEntryCarrierDetected, true);
                if (arrived.Task.IsCompleted)
                {
                    await arrived.Task;
                    return;
                }

                try
                {
                    StartMotor(entryStop.Token, reverse: true);
                }
                catch (OperationCanceledException) when (
                    entryStop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    await arrived.Task;
                }
                await arrived.Task.WaitAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                motor.Failure = exception;
            }
            finally
            {
                _io.InputChanged -= StopAtEntry;
            }
        }
        finally
        {
            _repeat = false;
        }
    }
}
