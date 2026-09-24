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
        ConveyorStation? source,
        ConveyorStation destination,
        CancellationToken cancellationToken)
    {
        var departingJob = source?.CurrentJob;
        var receiving = source is null;
        var timeout = TimeSpan.FromSeconds(_settings.TransferTimeoutSeconds);
        var timeoutMilliseconds = (int)timeout.TotalMilliseconds;
        var arrived = new TaskCompletionSource<ConveyorStation.Job>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                arrived.TrySetResult(destination.CurrentJob);
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
            if (source is not null)
            {
                source.RequireCurrentJob(departingJob!);
                if (!source.CarrierSeated
                    || !source.IsTransferAllowed
                    || !(ReferenceEquals(destination, _inspection.Station) ? _inspection.IsReceiveAllowed : destination.IsReceiveAllowed))
                {
                    throw new InvalidOperationException(
                        "Transfer requires the completed source carrier to remain seated and the destination to remain empty.");
                }
                await source.ReleaseAsync(cancellationToken);
                RequireSeatingPushPosition(destination);
            }
            else if (!_repeat && !EntryCarrierDetected)
            {
                SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, true);
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
                SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, false);
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
            EnterStep(State, target: "seating push", workId: destination.CurrentJob.Id, waitingFor:
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
            try
            {
                // HS2로 도착이 확인된 작업은 밀착 중 STOP해도 체결 결과를 이어받는다.
                // 감지 후 교체된 캐리어에는 이전 결과를 넘기지 않는다.
                if (source is not null
                    && arrived.Task.IsCompletedSuccessfully
                    && destination.CarrierPresent
                    && ReferenceEquals(destination.CurrentJob, await arrived.Task))
                {
                    source.TransferAssembliesTo(destination, departingJob!, await arrived.Task);
                }
            }
            finally
            {
                // 결과 인계 알림이 실패해도 벨트는 반드시 정지시킨다.
                StopOutputs(failure, OutputIo.MainConveyorRun, OutputIo.MainConveyorReadyToFront2);
            }
        }

        // S3는 플레이트 DOWN, 스토퍼 UP 상태에서 검사한다.
        if (!ReferenceEquals(destination, _inspection.Station))
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
        var departingJob = _inspection.Station.CurrentJob;
        var rearReleased = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var extraRun = TimeSpan.FromSeconds(_settings.RearSmemaOffDelaySeconds);
        var timeout = TimeSpan.FromSeconds(_settings.TransferTimeoutSeconds);
        void ObserveRear()
        {
            if (!DownstreamReady)
                rearReleased.TrySetResult(Stopwatch.GetTimestamp());
        }

        Changed += ObserveRear;
        Exception? failure = null;
        try
        {
            SetSmemaOutput(OutputIo.MainConveyorReadyToFront2, false);
            SetSmemaOutput(OutputIo.MainConveyorAvailableToRear, true);
            ObserveRear();
            if (rearReleased.Task.IsCompleted)
                return;
            _inspection.Station.RequireCurrentJob(departingJob);
            if (_repeat
                || IsNgTransferRequired
                || !_inspection.IsTransferAllowed
                || !_inspection.IsTransferAtWaitingPosition())
            {
                throw new MotionInterlockException(
                    "Rear discharge requires a completed carrier and the raised, clear inspection pickup at its waiting position.");
            }
            await _inspection.Station.ReleaseAsync(cancellationToken);

            if (rearReleased.Task.IsCompleted)
                return;
            _inspection.Station.RequireCurrentJob(departingJob);
            if (_inspection.Station.BackupPlate != StationCylinderState.Down
                || _inspection.Station.Stopper != StationCylinderState.Down
                || !_inspection.IsTransferAtWaitingPosition())
            {
                throw new MotionInterlockException(
                    "Rear discharge lost support release or inspection pickup clearance before starting the belt.");
            }
            EnterStep(MainConveyorState.DischargingInspectionCarrier, waitingFor: "Rear Ready=OFF");
            StartMotor(cancellationToken);
            try
            {
                await rearReleased.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new IoTimeoutException(
                    InputIo.MainConveyorReadyFromRear, false, (int)timeout.TotalMilliseconds);
            }

            EnterStep(MainConveyorState.DischargingInspectionCarrier,
                target: $"Rear Ready OFF; extra run {extraRun.TotalSeconds} s");
            // Only this discharge owns the OFF timestamp; STOP discards the remaining delay.
            var remaining = extraRun - Stopwatch.GetElapsedTime(await rearReleased.Task);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
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
