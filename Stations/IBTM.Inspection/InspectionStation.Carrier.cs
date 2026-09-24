using System;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed partial class InspectionStation
{
    // Scheduling ownership for this job only; never a physical position or restart checkpoint.
    private volatile ConveyorStation.Job? _inspectionRequestedJob;
    private volatile ConveyorStation.Job? _carrierSeatingRequestedJob;

    // Unfinished pickup/release ownership, not proof that material is present.
    public bool IsTransferPending
    {
        get;
        private set
        {
            if (field == value)
                return;
            field = value;
            NotifyChanged();
        }
    }

    public bool IsRaised => _io.GetInput(InputIo.NgCarrierPickupUp)
        && !_io.GetInput(InputIo.NgCarrierPickupDown);

    public bool IsClear => IsRaised && !IsTransferPending;

    public bool Enabled => _units.Inspection;

    public bool AtInspectionPosition => IsAtInspectionPosition();

    public bool IsAtInspectionPosition(bool? conveyorRunning = null)
    {
        return Station.CarrierPresent
            && Station.BackupPlate == StationCylinderState.Down
            && Station.Stopper == StationCylinderState.Up
            && !(conveyorRunning ?? _io.GetOutput(OutputIo.MainConveyorRun));
    }

    public bool InspectionRequested => ReferenceEquals(_inspectionRequestedJob, Station.CurrentJob);

    public bool CarrierSeatingRequested => ReferenceEquals(_carrierSeatingRequestedJob, Station.CurrentJob);

    public AxisPosition? WaitingPosition => _settings.WaitingPosition;

    public bool IsTransferAllowed => IsTransferAllowedFor();

    public bool IsTransferAllowedFor(bool? conveyorRunning = null)
    {
        return Station.CarrierPresent && Station.Completed
            && (IsAtInspectionPosition(conveyorRunning) || Station.CarrierSeated);
    }

    public bool IsReceiveAllowed => Station.IsReceiveAllowed && !IsTransferPending;

    public bool RouteToNg => !Enabled || HasNg;

    public bool HasNg
    {
        get
        {
            return Station.CarrierPresent
                && (Station.HasNg
                    || Enabled && Station.Completed
                        && !Station.Assemblies.Any(assembly => assembly.InspectionResult != AssemblyResult.Pending));
        }
    }

    internal bool IsWaitingForConveyor => Station.CarrierPresent && !Station.Completed
        && _units.MainConveyor && !InspectionRequested;

    internal bool IsReadyToInspect(bool? conveyorRunning = null)
    {
        return Station.CarrierPresent && !Station.Completed
            && (!_units.MainConveyor || InspectionRequested)
            && IsAtInspectionPosition(conveyorRunning) && IsClear;
    }

    public bool IsTransferAtWaitingPosition(bool live = true)
    {
        if (!IsClear)
            return false;
        if (!_units.IsMotionEnabled(MotionGroup.InspectionGantry))
            return true;
        return WaitingPosition is { } position
            && Motion.IsAt(position, live);
    }

    public void RequestInspection(ConveyorStation.Job job)
    {
        Station.RequireCurrentJob(job);
        if (!AtInspectionPosition || !IsClear)
            throw new InvalidOperationException("Inspection requires a present carrier, plate DOWN, stopper UP, stopped belt and clear pickup.");
        _inspectionRequestedJob = job;
        NotifyChanged();
    }

    public void ClearInspectionRequest()
    {
        _inspectionRequestedJob = null;
        _carrierSeatingRequestedJob = null;
        NotifyChanged();
    }

    public void RequestCarrierSeating(ConveyorStation.Job job)
    {
        Station.RequireCurrentJob(job);
        if (CarrierSeatingRequested)
            return;
        _carrierSeatingRequestedJob = job;
        NotifyChanged();
    }

    public void ClearCarrierSeatingRequest()
    {
        _carrierSeatingRequestedJob = null;
        NotifyChanged();
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        // An unexpected Open is grip loss, not a completed release of the pending transfer.
        if (input is InputIo.NgCarrierPickupUp
            or InputIo.NgCarrierPickupDown
            or InputIo.NgCarrierGripperOpen
            or InputIo.NgCarrierGripperClosed
            or InputIo.NgShuttleUp
            or InputIo.NgShuttleDown
            or InputIo.NgShuttleCarrierDetected)
        {
            NotifyChanged();
        }
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        if (output == OutputIo.MainConveyorRun)
            NotifyChanged();
    }
}
