using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Conveyor;

public sealed partial class MainConveyor
{
    private async Task TransferAsync(
        StationWork? sourceWork,
        StationWork destinationWork,
        CancellationToken cancellationToken)
    {
        var destination = destinationWork.Station;
        var departingJob = sourceWork?.CurrentJob;
        var receiving = sourceWork is null;
        var timeout = TimeSpan.FromSeconds(_settings.TransferTimeoutSeconds);
        var timeoutMilliseconds = (int)timeout.TotalMilliseconds;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var carrierLeft = new AsyncAutoResetEvent();
        void ObserveEntry(InputIo input, bool value)
        {
            if (input == InputIo.MainConveyorEntryCarrierDetected && value)
                entered.TrySetResult();
        }
        void ObserveArrival()
        {
            if (destination.IsHeatSinkPresent(HeatSinkSlot.HeatSink2))
                arrived.TrySetResult();
            if (arrived.Task.IsCompleted && !destination.CarrierPresent)
                carrierLeft.Set();
        }
        Exception? failure = null;
        try
        {
            StopOutputs(null, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
            // 목적지가 준비될 때까지 출발 캐리어는 벨트에서 분리해 둔다.
            await destination.PrepareToReceiveAsync(cancellationToken);
            RequireSeatingPushPosition(destination);
            if (sourceWork is not null)
            {
                sourceWork.RequireCurrentJob(departingJob!);
                if (!sourceWork.Station.CarrierSeated
                    || !sourceWork.IsTransferAllowed
                    || !destinationWork.IsReceiveAllowed)
                {
                    throw new InvalidOperationException(
                        "Transfer requires the completed source carrier to remain seated and the destination to remain empty.");
                }
                await sourceWork.Station.ReleaseAsync(cancellationToken);
                RequireSeatingPushPosition(destination);
            }
            else if (!_repeat && !EntryCarrierDetected)
            {
                _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorReadyToFront2, true);
            }

            destination.Changed += ObserveArrival;
            if (receiving)
                _io.InputChanged += ObserveEntry;
            ObserveArrival();
            if (receiving && EntryCarrierDetected)
                entered.TrySetResult();
            StartMotor(cancellationToken);
            if (receiving)
            {
                try
                {
                    await entered.Task.WaitAsync(timeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    throw new IoTimeoutException(InputIo.MainConveyorEntryCarrierDetected, true, timeoutMilliseconds);
                }
                _io.SetAutomaticSmemaOutput(OutputIo.MainConveyorReadyToFront2, false);
            }
            try
            {
                await arrived.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new IoTimeoutException(destination.HeatSink2Input, true, timeoutMilliseconds);
            }
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

            // 모터 OFF가 S3 검사를 깨우기 전에 출발 작업의 결과를 도착지에 전달한다.
            if (sourceWork is not null)
                sourceWork.TransferAssembliesTo(destinationWork, departingJob!);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (receiving)
                _io.InputChanged -= ObserveEntry;
            destination.Changed -= ObserveArrival;
            StopOutputs(failure, OutputIo.MainConveyorRun, OutputIo.MainConveyorReadyToFront2);
        }

        // S3는 플레이트 DOWN, 스토퍼 UP 상태에서 검사한다.
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

    private async Task DischargeInspectionAsync(CancellationToken cancellationToken)
    {
        // Only this awaited discharge owns the first detection; STOP discards it.
        var arrived = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstClear = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rearReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feedbackChanged = new AsyncAutoResetEvent();
        var clearDelay = TimeSpan.FromSeconds(_settings.ExitSensorClearDelaySeconds);
        var timeout = TimeSpan.FromSeconds(_settings.TransferTimeoutSeconds);
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
            if (!_repeat
                && !IsNgTransferRequired
                && _inspectionWork.IsTransferAllowed
                && _inspectionWork.IsTransferAtWaitingPosition())
            {
                await _inspectionWork.Station.ReleaseAsync(cancellationToken);
            }

            if (rearReleased.Task.IsCompleted)
                return;
            TraceStep(MainConveyorState.DischargingInspectionCarrier, waitingFor:
                $"Rear Ready=OFF OR exit detected then first OFF + {clearDelay.TotalSeconds} s and sensor=OFF");
            var started = Stopwatch.GetTimestamp();
            StartMotor(cancellationToken);
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
                        InputIo.MainConveyorExitCarrierDetected, waitingForDetection, (int)timeout.TotalMilliseconds);
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
        _io.SetOutput(OutputIo.MainConveyorNormalSpeed, true);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorForward, !reverse);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorRun, true);
    }
}
