using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionWork : StationWork
{
    private readonly IInspectionGantryClearance? _gantryClearance;

    public InspectionWork(
        ConveyorStation station,
        IInspectionGantryClearance? gantryClearance) : base(station)
    {
        _gantryClearance = gantryClearance;
        if (gantryClearance is not null)
        {
            gantryClearance.Changed += NotifyChanged;
        }
    }

    public override bool CanReceive =>
        base.CanReceive
        && (_gantryClearance?.IsClear ?? true);

    public override bool HasNg =>
        CarrierPresent
        && (base.HasNg
            || Completed
            && !HeatSinkPresent(HeatSinkSlot.HeatSink1)
            && !HeatSinkPresent(HeatSinkSlot.HeatSink2));

    public InspectionState State
    {
        get
        {
            if (!CarrierPresent)
            {
                return InspectionState.WaitingForCarrier;
            }

            if (Completed)
            {
                return InspectionState.WaitingForTransfer;
            }

            if (!CarrierSeated)
            {
                return InspectionState.WaitingForSeat;
            }

            return _gantryClearance?.IsClear == false
                ? InspectionState.WaitingForGantry
                : InspectionState.ReadyToInspect;
        }
    }

    public void RestartInspection()
    {
        foreach (var assembly in Assemblies)
        {
            assembly.ResetInspection();
        }

        Restart();
    }

}
