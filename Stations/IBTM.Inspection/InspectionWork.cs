using System;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionWork : StationWork, INgCarrierTransferFeedback
{
    private readonly IIoService _io;
    private readonly NgCarrierTransferSettings _transferSettings;
    // Scheduling ownership for this job only; never a physical position or restart checkpoint.
    private volatile Job? _inspectionRequestedJob;
    private volatile Job? _carrierSeatingRequestedJob;

    public InspectionWork(
        IIoService io,
        IXyMotion motion,
        NgCarrierTransferSettings transferSettings,
        UnitSettings units) : base(ConveyorStation.CreateInspection(io), units)
    {
        _io = io;
        Feedback = motion;
        Motion = new(motion);
        _transferSettings = transferSettings;
        motion.StateChanged += NotifyChanged;
        io.InputChanged += OnInputChanged;
        io.OutputChanged += OnOutputChanged;
    }

    public IXyMotion Feedback { get; }

    public MotionStatus Motion { get; }

    // Unfinished pickup/release ownership, not proof that material is present.
    public bool IsTransferPending
    {
        get;
        internal set
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

    public bool CarrierSeatingRequested => ReferenceEquals(_carrierSeatingRequestedJob, CurrentJob);

    public bool PickupClear => IsClear;

    public AxisPosition? WaitingPosition => _transferSettings.GetCarrierPickupPosition();

    public override bool IsTransferAllowed => IsTransferAllowedFor();

    public bool IsTransferAllowedFor(bool? conveyorRunning = null)
    {
        return Station.CarrierPresent && Completed
            && (IsAtInspectionPosition(conveyorRunning) || Station.CarrierSeated);
    }

    public override bool IsReceiveAllowed => base.IsReceiveAllowed
        && (!Units.Inspection || !IsTransferPending);

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

    internal bool IsWaitingForConveyor => Station.CarrierPresent && !Completed
        && Units.MainConveyor && !InspectionRequested;

    internal bool IsReadyToInspect(bool? conveyorRunning = null)
    {
        return Station.CarrierPresent && !Completed
            && (!Units.MainConveyor || InspectionRequested)
            && IsAtInspectionPosition(conveyorRunning) && IsClear;
    }

    public bool IsTransferAtWaitingPosition(bool live = true)
    {
        if (!Units.IsMotionEnabled(MotionGroup.InspectionGantry))
            return true;
        return IsClear
            && WaitingPosition is { } position
            && IsAt(position, live);
    }

    public bool IsAt(AxisPosition position, bool live = true)
    {
        var current = Motion.ReadPosition(live);
        return Motion.IsSettled(live, MotionAxis.X, MotionAxis.Y)
            && Math.Abs(current.X - position.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - position.Y) <= MotionService.PositionToleranceMillimeters;
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
        _carrierSeatingRequestedJob = null;
        NotifyChanged();
    }

    public void RequestCarrierSeating(Job job)
    {
        RequireCurrentJob(job);
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
            or InputIo.NgCarrierDetected
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
