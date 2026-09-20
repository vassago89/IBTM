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
        if (CarrierCount > 1 || ExitCarrierDetected)
            throw new InvalidOperationException("Main conveyor return requires one carrier and a clear exit.");
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
            using var motor = new ConveyorRun(_io, OutputIo.MainConveyorRun, cancellationToken, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
            try
            {
                await ReturnCarrierAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                motor.Failure = exception;
            }
        }
        finally
        {
            _repeat = false;
        }
    }

    private async Task ReturnCarrierAsync(CancellationToken cancellationToken)
    {
        if (!EntryCarrierDetected
            && !_placementWork.Station.CarrierPresent
            && !_boltFasteningWork.Station.CarrierPresent
            && !_inspectionWork.Station.CarrierPresent)
        {
            throw new InvalidOperationException("Return carrier position is unknown. Restore carrier presence before restarting.");
        }

        await Task.WhenAll(
            _placementWork.Station.ReleaseAsync(cancellationToken),
            _boltFasteningWork.Station.ReleaseAsync(cancellationToken),
            _inspectionWork.Station.ReleaseAsync(cancellationToken));

        if (EntryCarrierDetected)
            return;

        Exception? failure = null;
        try
        {
            StartMotor(cancellationToken, reverse: true);
            await _io.WaitForInputAsync(
                InputIo.MainConveyorEntryCarrierDetected,
                true,
                (int)(_settings.TransferTimeoutSeconds * 1000),
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
}
