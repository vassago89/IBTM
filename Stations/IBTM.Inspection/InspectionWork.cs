using System;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionWork : StationWork
{
    private readonly IIoService _io;
    private readonly INgCarrierTransferFeedback _transferFeedback;
    private readonly InspectionGantry _gantry;
    private readonly NgCarrierTransferSettings _transferSettings;
    // Scheduling ownership for this job only; never a physical position or restart checkpoint.
    private volatile Job? _inspectionRequestedJob;

    public InspectionWork(
        IIoService io,
        INgCarrierTransferFeedback transferFeedback,
        InspectionGantry gantry,
        NgCarrierTransferSettings transferSettings,
        UnitSettings units) : base(ConveyorStation.CreateInspection(io), units)
    {
        _io = io;
        _transferFeedback = transferFeedback;
        _gantry = gantry;
        _transferSettings = transferSettings;
        transferFeedback.Changed += NotifyChanged;
        // Conveyor release depends on the transfer's actual waiting position.
        gantry.Feedback.StateChanged += NotifyChanged;
        io.OutputChanged += OnOutputChanged;
    }

    public override bool Enabled => Units.Inspection;

    public bool AtInspectionPosition => IsAtInspectionPosition();

    public bool IsAtInspectionPosition(bool? conveyorRunning = null)
    {
        return Station.CarrierPresent
            && Station.BackupPlate == StationCylinderState.Down
            && Station.Stopper == StationCylinderState.Up
            && !(conveyorRunning ?? _io.GetOutput(OutputIo.MainConveyorRun));
    }

    public bool InspectionRequested => ReferenceEquals(_inspectionRequestedJob, CurrentJob);

    public bool PickupClear => _transferFeedback.IsClear;

    public override bool IsTransferAllowed => IsTransferAllowedFor();

    public bool IsTransferAllowedFor(bool? conveyorRunning = null)
    {
        return Station.CarrierPresent && Completed
            && (IsAtInspectionPosition(conveyorRunning) || Station.CarrierSeated);
    }

    public override bool IsReceiveAllowed => base.IsReceiveAllowed
        && (!Units.NgCarrierTransfer || !_transferFeedback.CarrierDetected);

    public bool RouteToNg => !Enabled || HasNg;

    public override bool HasNg
    {
        get
        {
            return Station.CarrierPresent
                && (base.HasNg
                    || Enabled && Completed
                        && !Assemblies.Any(assembly => assembly.InspectionResult != AssemblyResult.Pending));
        }
    }

    internal InspectionWorkState State => GetState();

    internal InspectionWorkState GetState(bool? conveyorRunning = null)
    {
        switch (true)
        {
            case true when !Station.CarrierPresent:
                return InspectionWorkState.WaitingForCarrier;
            case true when Completed:
                return InspectionWorkState.WaitingForTransfer;
            case true when Units.MainConveyor && !InspectionRequested:
                return InspectionWorkState.WaitingForConveyor;
            case true when !IsAtInspectionPosition(conveyorRunning):
                return InspectionWorkState.WaitingForInspectionPosition;
            default:
                return !_transferFeedback.IsClear
                    ? InspectionWorkState.WaitingForGantry
                    : InspectionWorkState.ReadyToInspect;
        }
    }

    public bool IsTransferAtWaitingPosition(bool live = true)
    {
        if (!Units.IsMotionEnabled(MotionGroup.InspectionGantry))
            return true;
        return _transferFeedback.IsClear
            && _transferSettings.GetCarrierPickupPosition() is { } position
            && _gantry.IsAt(position, live);
    }

    public void RequestInspection(Job job)
    {
        RequireCurrentJob(job);
        if (!AtInspectionPosition || !PickupClear)
            throw new InvalidOperationException("Inspection requires a present carrier, plate DOWN, stopper UP, stopped belt and clear pickup.");
        _inspectionRequestedJob = job;
        NotifyChanged();
    }

    public void ClearInspectionRequest()
    {
        _inspectionRequestedJob = null;
        NotifyChanged();
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        if (output == OutputIo.MainConveyorRun)
            NotifyChanged();
    }
}
