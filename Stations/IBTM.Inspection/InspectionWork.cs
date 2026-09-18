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
    private readonly Func<bool>? _isGantryEnabled;
    private readonly Func<bool>? _isConveyorEnabled;
    // Scheduling ownership for this job only; never a physical position or restart checkpoint.
    private volatile Job? _inspectionRequestedJob;

    public InspectionWork(
        IIoService io,
        INgCarrierTransferFeedback transferFeedback,
        InspectionGantry gantry,
        NgCarrierTransferSettings transferSettings,
        Func<bool>? isEnabled = null,
        Func<bool>? isGantryEnabled = null,
        Func<bool>? isConveyorEnabled = null) : base(ConveyorStation.Inspection(io), isEnabled)
    {
        _io = io;
        _transferFeedback = transferFeedback;
        _gantry = gantry;
        _transferSettings = transferSettings;
        _isGantryEnabled = isGantryEnabled;
        _isConveyorEnabled = isConveyorEnabled;
        transferFeedback.Changed += NotifyChanged;
        // Conveyor release depends on the transfer's actual waiting position.
        gantry.Feedback.StateChanged += NotifyChanged;
        io.OutputChanged += OnOutputChanged;
    }

    public bool AtInspectionPosition
    {
        get
        {
            return CarrierPresent
                && BackupPlate == StationCylinderState.Down
                && Stopper == StationCylinderState.Up
                && !_io.GetOutput(OutputIo.MainConveyorRun);
        }
    }

    public bool InspectionRequested
    {
        get
        {
            return ReferenceEquals(_inspectionRequestedJob, CurrentJob);
        }
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

    public bool PickupClear
    {
        get
        {
            return _transferFeedback.IsClear;
        }
    }

    public bool IsTransferAtWaitingPosition(bool live = true)
    {
        if (!(_isGantryEnabled?.Invoke() ?? Enabled))
            return true;
        return _transferFeedback.IsClear
            && _transferSettings.GetCarrierPickupPosition() is { } position
            && _gantry.IsAt(position, live);
    }

    public override bool Completed
    {
        get
        {
            return Enabled ? base.Completed : CarrierPresent;
        }
    }

    public override bool CanTransfer
    {
        get
        {
            return CarrierPresent && Completed && (AtInspectionPosition || CarrierSeated);
        }
    }

    public override bool CanReceive
    {
        get
        {
            return base.CanReceive && !_transferFeedback.CarrierDetected;
        }
    }

    public bool RouteToNg
    {
        get
        {
            return !Enabled || HasNg;
        }
    }

    public override bool HasNg
    {
        get
        {
            return CarrierPresent
                && (base.HasNg
                    || Enabled && Completed
                        && !Assemblies.Any(assembly => assembly.InspectionResult != AssemblyResult.Pending));
        }
    }

    internal InspectionWorkState State
    {
        get
        {
            if (!CarrierPresent)
            {
                return InspectionWorkState.WaitingForCarrier;
            }

            if (Completed)
            {
                return InspectionWorkState.WaitingForTransfer;
            }

            if ((_isConveyorEnabled?.Invoke() ?? false) && !InspectionRequested)
                return InspectionWorkState.WaitingForConveyor;

            if (!AtInspectionPosition)
            {
                return InspectionWorkState.WaitingForInspectionPosition;
            }

            return !_transferFeedback.IsClear
                ? InspectionWorkState.WaitingForGantry
                : InspectionWorkState.ReadyToInspect;
        }
    }

    private void OnOutputChanged(OutputIo output, bool _)
    {
        if (output == OutputIo.MainConveyorRun)
            NotifyChanged();
    }
}
