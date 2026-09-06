using System.ComponentModel;

namespace IBTM.Inspection;

public enum InspectionStationState
{
    [Description("Waiting")]
    Waiting,

    [Description("Moving to Bolt")]
    MovingToBolt,

    [Description("Inspecting Bolt")]
    InspectingBolt,

    [Description("Completing Inspection")]
    CompletingInspection,

    [Description("Moving Transfer to Carrier")]
    MovingTransferToCarrier,

    [Description("Lowering Transfer at Carrier")]
    LoweringTransferAtCarrier,

    [Description("Closing NG Transfer Gripper")]
    ClosingTransferGripper,

    [Description("Waiting for Carrier Grip")]
    WaitingForCarrierGrip,

    [Description("Raising NG Transfer")]
    RaisingCarrierTransfer,

    [Description("Moving Transfer to Shuttle")]
    MovingTransferToShuttle,

    [Description("Waiting for Shuttle")]
    WaitingForShuttleReady,

    [Description("Lowering Transfer at Shuttle")]
    LoweringTransferAtShuttle,

    [Description("Opening NG Transfer Gripper")]
    OpeningTransferGripper,

    [Description("Waiting for Shuttle Carrier")]
    WaitingForShuttleCarrier,
}
