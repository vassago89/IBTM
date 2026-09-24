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

    [Description("Returning and Completing Inspection")]
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

    [Description("Moving to NG Pickup and Raising Carrier")]
    SeatingCarrier,

    [Description("Waiting for Other Carrier Transfers Before Inspection")]
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

public enum NgTransferDestination
{
    [Description("Station 3")]
    Station,
    [Description("Shuttle")]
    Shuttle,
}

public enum NgTransferLiftState
{
    [Description("Up")]
    Up,

    [Description("Between")]
    Between,

    [Description("Down")]
    Down,
}

public enum NgTransferGripperState
{
    [Description("Open")]
    Open,

    [Description("Between")]
    Between,

    [Description("Closed")]
    Closed,
}
