using System;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionWork : StationWork
{
    private readonly INgCarrierTransferFeedback _transferFeedback;

    public InspectionWork(
        ConveyorStation station,
        INgCarrierTransferFeedback transferFeedback,
        Func<bool>? isEnabled = null) : base(station, isEnabled)
    {
        _transferFeedback = transferFeedback;
        transferFeedback.Changed += NotifyChanged;
    }

    public override bool CanReceive =>
        base.CanReceive
        && _transferFeedback.IsClear;

    public bool RouteToNg => !Enabled || HasNg;

    public override bool HasNg =>
        CarrierPresent
        && (base.HasNg
            || Completed
            && (Enabled
                ? !Assemblies.Any(assembly =>
                    assembly.InspectionResult != AssemblyResult.Pending)
                : !HeatSinkPresent(HeatSinkSlot.HeatSink1)
                  && !HeatSinkPresent(HeatSinkSlot.HeatSink2)));

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

            if (!CarrierSeated)
            {
                return InspectionWorkState.WaitingForSeat;
            }

            return !_transferFeedback.IsClear
                ? InspectionWorkState.WaitingForGantry
                : InspectionWorkState.ReadyToInspect;
        }
    }

    internal void RestartInspection()
    {
        foreach (var assembly in Assemblies)
        {
            assembly.ResetInspection();
        }

        Restart();
    }

}
