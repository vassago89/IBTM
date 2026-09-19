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

    [Description("Transferring NG Carrier")]
    TransferringNgCarrier,

    [Description("Waiting for Shuttle")]
    WaitingForShuttleReady,

    [Description("Holding Carrier at Shuttle")]
    HoldingCarrierAtShuttle,

    [Description("Teach Inspection FOV / ROI")]
    FovTeachingRequired,

    [Description("Returning to NG Pickup Waiting Position")]
    ReturningToNgPickup,

    [Description("Waiting for Other Carrier Transfers Before Inspection")]
    WaitingForConveyor,
}
