using System.ComponentModel;

namespace IBTM.Inspection;

public enum InspectionStationState
{
    [Description("Waiting")]
    Waiting,

    [Description("Teach Inspection Regions")]
    TeachingRequired,

    [Description("Inspecting")]
    InspectingPoint,

    [Description("Returning / completing inspection")]
    CompletingInspection,

    [Description("Waiting for Transfer Supports")]
    WaitingForDestination,

    [Description("Preparing Pickup")]
    PreparingTransfer,

    [Description("Picking Carrier")]
    PickingCarrier,

    [Description("Moving and Placing Carrier")]
    PlacingCarrier,

    [Description("Returning to Waiting Position")]
    ReturningToWaitingPosition,

    [Description("NG pickup: move / raise carrier")]
    SeatingCarrier,

    [Description("Waiting for carrier transfers")]
    WaitingForConveyor,

    [Description("Disabled")]
    Disabled,

    [Description("Preparing Inspection Points")]
    PreparingInspection,

    [Description("Waiting for NG Shuttle Down")]
    WaitingForShuttleDown,
}
