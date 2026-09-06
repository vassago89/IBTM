using System.ComponentModel;

namespace IBTM.Inspection;

internal enum InspectionWorkState
{
    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Waiting for Carrier Seat")]
    WaitingForSeat,

    [Description("Waiting for Inspection Gantry")]
    WaitingForGantry,

    [Description("Ready to Inspect")]
    ReadyToInspect,

    [Description("Waiting for Transfer")]
    WaitingForTransfer,
}
