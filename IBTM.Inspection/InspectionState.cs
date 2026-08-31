using System.ComponentModel;

namespace IBTM.Inspection;

public enum InspectionState
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

public enum InspectionProcessState
{
    [Description("Waiting")]
    Waiting,

    [Description("Moving to Bolt")]
    MovingToBolt,

    [Description("Inspecting Bolt")]
    InspectingBolt,

    [Description("Completing Carrier")]
    CompletingCarrier,
}
