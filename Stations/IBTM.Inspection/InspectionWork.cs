using System;
using System.Linq;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.Inspection;

public sealed class InspectionWork : StationWork
{
    private readonly IIoService _io;
    private readonly NgCarrierTransfer _transfer;
    private readonly NgCarrierTransferSettings _transferSettings;
    private readonly RecipeManager _recipes;
    // Scheduling ownership for this job only; never a physical position or restart checkpoint.
    private volatile Job? _inspectionRequestedJob;
    private volatile Job? _carrierSeatingRequestedJob;

    public InspectionWork(
        IIoService io,
        NgCarrierTransfer transfer,
        NgCarrierTransferSettings transferSettings,
        RecipeManager recipes,
        UnitSettings units) : base(transfer.Station, units)
    {
        _io = io;
        _transfer = transfer;
        _transferSettings = transferSettings;
        _recipes = recipes;
        transfer.Changed += NotifyChanged;
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

    public bool CarrierSeatingRequested => ReferenceEquals(_carrierSeatingRequestedJob, CurrentJob);

    public bool PickupClear => _transfer.IsClear;

    public AxisPosition? WaitingPosition => Enabled
        ? _recipes.Current.InspectionWaitingPosition
        : _transferSettings.GetCarrierPickupPosition();

    public override bool IsTransferAllowed => IsTransferAllowedFor();

    public bool IsTransferAllowedFor(bool? conveyorRunning = null)
    {
        return Station.CarrierPresent && Completed
            && (IsAtInspectionPosition(conveyorRunning) || Station.CarrierSeated);
    }

    public override bool IsReceiveAllowed => base.IsReceiveAllowed
        && (!Units.NgCarrierTransfer || !_transfer.IsTransferPending);

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
            && IsAtInspectionPosition(conveyorRunning) && _transfer.IsClear;
    }

    public bool IsTransferAtWaitingPosition(bool live = true)
    {
        if (!Units.IsMotionEnabled(MotionGroup.InspectionGantry))
            return true;
        return _transfer.IsClear
            && WaitingPosition is { } position
            && _transfer.IsAt(position, live);
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

    private void OnOutputChanged(OutputIo output, bool value)
    {
        if (output == OutputIo.MainConveyorRun)
            NotifyChanged();
    }
}
