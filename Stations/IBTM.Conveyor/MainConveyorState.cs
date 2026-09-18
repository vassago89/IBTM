using System.ComponentModel;

namespace IBTM.Conveyor;

public enum MainConveyorState
{
    [Description("Idle")]
    Idle,

    [Description("Waiting at infeed")]
    WaitingForFrontCarrier,

    [Description("Waiting for downstream")]
    WaitingForRearEquipment,

    [Description("Seating S1 carrier")]
    SeatingPcbPlacementCarrier,

    [Description("Seating S2 carrier")]
    SeatingBoltFasteningCarrier,

    [Description("Seating S3 carrier")]
    SeatingInspectionCarrier,

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

    [Description("Waiting for S2 fastening completion")]
    WaitingForBoltFastening,

    [Description("Waiting for S3 / NG pickup to clear")]
    WaitingForInspectionClear,
}
