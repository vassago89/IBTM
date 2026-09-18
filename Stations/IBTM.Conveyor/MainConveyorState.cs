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

    [Description("Manual clear required · empty conveyor, then RESET")]
    ManualClearRequired,

    [Description("Conveyor RUN output ON")]
    Running,
}
