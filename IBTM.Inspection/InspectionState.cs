using System.ComponentModel;

namespace IBTM.Inspection;

public enum InspectionState
{
    [Description("Waiting for Carrier Jig")]
    WaitingForCarrier,

    [Description("Waiting for Carrier Seat")]
    WaitingForSeat,

    [Description("Ready to Inspect")]
    ReadyToInspect,

    [Description("Waiting for Transfer")]
    WaitingForTransfer,
}
