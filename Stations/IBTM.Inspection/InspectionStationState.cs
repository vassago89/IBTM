using System.ComponentModel;

namespace IBTM.Inspection;

public enum InspectionStationState
{
    [Description("Waiting")]
    Waiting,

    [Description("Teach Data Matrix Regions")]
    BarcodeTeachingRequired,

    [Description("Reading Data Matrix")]
    ReadingBarcode,

    [Description("Inspecting Bolt")]
    InspectingBolt,

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

    [Description("Transfer Complete")]
    TransferCompleted,

    [Description("Carrier Held at Destination")]
    HoldingAtDestination,

    [Description("Teach Inspection FOV / ROI")]
    FovTeachingRequired,

    [Description("Returning to Waiting Position")]
    ReturningToWaitingPosition,

    [Description("NG pickup: move / raise carrier")]
    SeatingCarrier,

    [Description("Waiting for carrier transfers")]
    WaitingForConveyor,

    [Description("Disabled")]
    Disabled,

    [Description("Preparing Inspection Supports")]
    PreparingInspectionPosition,

    [Description("Preparing Inspection Points")]
    PreparingInspection,

    [Description("Waiting for NG Shuttle Down")]
    WaitingForShuttleDown,
}
