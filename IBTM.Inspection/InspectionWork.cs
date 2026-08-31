using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionWork : StationWork
{
    private readonly IInspectionGantryClearance? _gantryClearance;

    public InspectionWork(
        IIoService io,
        IInspectionGantryClearance? gantryClearance) : base(
        io,
        InputIo.InspectionCarrierPresent,
        InputIo.InspectionBackupPlateUp,
        InputIo.InspectionStopperDown,
        InputIo.InspectionHeatSink1Present,
        InputIo.InspectionHeatSink2Present)
    {
        _gantryClearance = gantryClearance;
        if (gantryClearance is not null)
        {
            gantryClearance.Changed += NotifyChanged;
        }
    }

    public override bool CanReceive =>
        base.CanReceive
        && (_gantryClearance?.Available ?? true);

    public override bool HasNg =>
        CarrierPresent
        && (base.HasNg
            || Completed
            && !HeatSinkPresent(HeatSinkSlot.HeatSink1)
            && !HeatSinkPresent(HeatSinkSlot.HeatSink2));

    public InspectionState State =>
        !CarrierPresent
            ? InspectionState.WaitingForCarrier
            : Completed
                ? InspectionState.WaitingForTransfer
                : !Ready
                    ? InspectionState.WaitingForSeat
                    : _gantryClearance?.Available == false
                        ? InspectionState.WaitingForGantry
                        : InspectionState.ReadyToInspect;

    public void RestartInspection()
    {
        foreach (var assembly in Assemblies)
        {
            assembly.ResetInspection();
        }

        Restart();
    }

}
