using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Conveyor;

public sealed partial class MainConveyor
{
    private async Task ReturnCarrierAsync(CancellationToken cancellationToken)
    {
        if (!EntryCarrierDetected
            && !_placement.CarrierPresent
            && !_boltFastening.CarrierPresent
            && !_inspection.CarrierPresent)
        {
            throw new InvalidOperationException("Return carrier position is unknown. Restore carrier presence before restarting.");
        }

        await Task.WhenAll(
            _placement.ReleaseAsync(cancellationToken),
            _boltFastening.ReleaseAsync(cancellationToken),
            _inspection.ReleaseAsync(cancellationToken));

        if (EntryCarrierDetected)
            return;

        Exception? failure = null;
        try
        {
            StartMotor(cancellationToken, reverse: true);
            await _io.WaitForInputAsync(
                InputIo.MainConveyorEntryCarrierDetected,
                true,
                cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            StopOutputs(failure, OutputIo.MainConveyorRun);
        }
    }

    private async Task ReceiveAtPlacementAsync(CancellationToken cancellationToken)
    {
        _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorAvailableToRear, false);
        Exception? failure = null;
        try
        {
            await _placement.PrepareToReceiveAsync(cancellationToken);
            RequireSeatingPushPosition(_placement);
            if (!_repeat && !EntryCarrierDetected)
                _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorReadyToFront2, true);

            await RunToStationAsync(_placementWork, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            StopOutputs(failure, OutputIo.MainConveyorRun, OutputIo.MainConveyorReadyToFront2);
        }

        if (!_placement.CarrierPresent)
            throw new InvalidOperationException("Carrier presence was lost after the seating push.");
        await _placement.SeatAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task MoveCarrierAsync(
        StationWork sourceWork,
        StationWork destinationWork,
        CancellationToken cancellationToken)
    {
        var source = sourceWork.Station;
        var destination = destinationWork.Station;
        var departingJob = sourceWork.CurrentJob;
        Exception? failure = null;
        try
        {
            StopOutputs(null, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
            // Keep the carrier off the belt until its destination is ready.
            await destination.PrepareToReceiveAsync(cancellationToken);
            RequireSeatingPushPosition(destination);
            sourceWork.RequireCurrentJob(departingJob);
            if (!sourceWork.CarrierSeated
                || !sourceWork.CanTransfer
                || !destinationWork.CanReceive)
            {
                throw new InvalidOperationException(
                    "Transfer requires the completed source carrier to remain seated and the destination to remain empty.");
            }
            await source.ReleaseAsync(cancellationToken);
            RequireSeatingPushPosition(destination);
            await RunToStationAsync(destinationWork, cancellationToken);
            if (!destination.CarrierPresent)
                throw new InvalidOperationException("Carrier presence was lost after the seating push.");
            cancellationToken.ThrowIfCancellationRequested();
            // Commit after HS2 + push, before STOP can wake S3 inspection on the
            // lowered plate. The original source job owns these results throughout.
            sourceWork.TransferAssembliesTo(destinationWork, departingJob);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            StopOutputs(failure, OutputIo.MainConveyorRun);
        }

        // S3 inspects at conveyor height, held by the raised stopper.
        if (!ReferenceEquals(destinationWork, _inspectionWork))
            await destination.SeatAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void RequireSeatingPushPosition(ConveyorStation destination)
    {
        if (destination.BackupPlate != StationCylinderState.Down
            || destination.Stopper != StationCylinderState.Up)
        {
            throw new InvalidOperationException(
                "Seating push requires backup plate DOWN and stopper UP feedback. Check the stopped carrier position.");
        }
    }

    private async Task RunToStationAsync(
        StationWork destinationWork,
        CancellationToken cancellationToken)
    {
        var destination = destinationWork.Station;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var carrierLeft = new AsyncAutoResetEvent();
        var receiving = ReferenceEquals(destination, _placement);
        void ObserveEntry(InputIo input, bool value)
        {
            if (input == InputIo.MainConveyorEntryCarrierDetected && value)
                entered.TrySetResult();
        }
        void ObserveArrival()
        {
            if (destinationWork.HeatSinkPresent(HeatSinkSlot.HeatSink2))
                arrived.TrySetResult();
            if (arrived.Task.IsCompleted && !destination.CarrierPresent)
                carrierLeft.Set();
        }
        destination.Changed += ObserveArrival;
        if (receiving)
            _io.InputChanged += ObserveEntry;
        try
        {
            ObserveArrival();
            if (receiving && EntryCarrierDetected)
                entered.TrySetResult();
            StartMotor(cancellationToken);
            if (receiving)
            {
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromMilliseconds(_io.TimeoutMilliseconds), cancellationToken);
                }
                catch (TimeoutException)
                {
                    throw new IoTimeoutException(InputIo.MainConveyorEntryCarrierDetected, true, _io.TimeoutMilliseconds);
                }
                _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorReadyToFront2, false);
            }
            await arrived.Task.WaitAsync(cancellationToken);
            if (!destination.CarrierPresent)
                carrierLeft.Set();
            TraceStep(State, target: "seating push", workId: destinationWork.CurrentJob.Id, waitingFor:
                $"Heat Sink 2 detected; push for {_settings.CarrierStopDelaySeconds} s");
            var lostCarrier = await carrierLeft.WaitAsync(
                TimeSpan.FromSeconds(_settings.CarrierStopDelaySeconds),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (lostCarrier || !destination.CarrierPresent)
            {
                throw new InvalidOperationException("Carrier presence was lost during the seating push.");
            }
        }
        finally
        {
            if (receiving)
                _io.InputChanged -= ObserveEntry;
            destination.Changed -= ObserveArrival;
        }
    }

    private async Task DischargeInspectionAsync(CancellationToken cancellationToken)
    {
        // Only this awaited discharge owns the first detection; STOP discards it.
        var arrived = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstClear = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rearReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feedbackChanged = new AsyncAutoResetEvent();
        var clearDelay = TimeSpan.FromSeconds(_settings.ExitSensorClearDelaySeconds);
        void ObserveRear()
        {
            if (!DownstreamReady)
                rearReleased.TrySetResult();
            feedbackChanged.Set();
        }
        void ObserveExit(InputIo input, bool value)
        {
            if (input != InputIo.MainConveyorExitCarrierDetected)
                return;
            if (value)
                arrived.TrySetResult(Stopwatch.GetTimestamp());
            else if (arrived.Task.IsCompleted)
                firstClear.TrySetResult(Stopwatch.GetTimestamp());
            feedbackChanged.Set();
        }

        _io.InputChanged += ObserveExit;
        Changed += ObserveRear;
        Exception? failure = null;
        try
        {
            _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorReadyToFront2, false);
            _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorAvailableToRear, true);
            ObserveRear();
            if (rearReleased.Task.IsCompleted)
                return;
            if (ExitCarrierDetected)
                arrived.TrySetResult(Stopwatch.GetTimestamp());
            if (CanReleaseInspection())
            {
                await _inspection.ReleaseAsync(cancellationToken);
            }

            if (rearReleased.Task.IsCompleted)
                return;
            TraceStep(MainConveyorState.DischargingInspectionCarrier, waitingFor:
                $"Rear Ready=OFF OR exit detected then first OFF + {clearDelay.TotalSeconds} s and sensor=OFF");
            var started = Stopwatch.GetTimestamp();
            StartMotor(cancellationToken);
            var timeout = TimeSpan.FromMilliseconds(_io.TimeoutMilliseconds);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rearReleased.Task.IsCompleted)
                {
                    TraceStep(MainConveyorState.DischargingInspectionCarrier, target: "Rear Ready OFF; stopping");
                    return;
                }

                TimeSpan remaining;
                var waitingForDetection = !arrived.Task.IsCompleted;
                if (waitingForDetection)
                {
                    remaining = timeout - Stopwatch.GetElapsedTime(started);
                }
                else if (!firstClear.Task.IsCompleted)
                {
                    if (!ExitCarrierDetected)
                    {
                        firstClear.TrySetResult(Stopwatch.GetTimestamp());
                        continue;
                    }
                    remaining = timeout - Stopwatch.GetElapsedTime(await arrived.Task);
                }
                else
                {
                    // Plasma's margin starts at the first OFF after detection.
                    // Later ON pulses keep this timer, but cannot complete the exit.
                    var elapsed = Stopwatch.GetElapsedTime(await firstClear.Task);
                    if (elapsed >= clearDelay && !ExitCarrierDetected)
                    {
                        TraceStep(MainConveyorState.DischargingInspectionCarrier,
                            target: "Exit margin elapsed and sensor OFF; stopping");
                        return;
                    }
                    remaining = elapsed < clearDelay
                        ? clearDelay - elapsed
                        : clearDelay + timeout - elapsed;
                }
                if (remaining <= TimeSpan.Zero)
                    throw new IoTimeoutException(
                        InputIo.MainConveyorExitCarrierDetected, waitingForDetection, _io.TimeoutMilliseconds);
                await feedbackChanged.WaitAsync(remaining, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _io.InputChanged -= ObserveExit;
            Changed -= ObserveRear;
            StopOutputs(failure, OutputIo.MainConveyorRun, OutputIo.MainConveyorAvailableToRear);
        }
    }

    private void StartMotor(CancellationToken cancellationToken, bool reverse = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorForward, !reverse);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorRun, true);
    }
}
