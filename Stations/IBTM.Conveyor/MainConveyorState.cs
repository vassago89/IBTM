using System.ComponentModel;

namespace IBTM.Conveyor;

public enum MainConveyorState
{
    [Description("Waiting at infeed")]
    WaitingForFrontCarrier,

    [Description("Waiting for downstream")]
    WaitingForRearEquipment,

    [Description("Seating S1 / S2 carriers")]
    SeatingCarriers,

    [Description("Raising S3")]
    RaisingInspectionCarrier,

    [Description("Receiving at infeed")]
    ReceivingFrontCarrier,

    [Description("Moving S1 → S2")]
    MovingPcbPlacementToBoltFastening,

    [Description("Moving S2 → S3")]
    MovingBoltFasteningToInspection,

    [Description("Discharging S3")]
    DischargingInspectionCarrier,

    [Description("Conveyor RUN output ON")]
    Running,

    [Description("Waiting for S2 fastening")]
    WaitingForBoltFastening,

    [Description("Waiting for S1 placement")]
    WaitingForPcbPlacement,

    [Description("Waiting for S3 / NG pickup to clear")]
    WaitingForInspectionClear,

    [Description("Preparing S3: plate DOWN")]
    PreparingInspectionCarrier,

    [Description("Waiting for S3 inspection")]
    WaitingForInspection,

    [Description("Waiting for transfer at NG pickup")]
    WaitingForInspectionTransfer,

    [Description("Returning carrier to entry")]
    ReturningToEntry,
}
