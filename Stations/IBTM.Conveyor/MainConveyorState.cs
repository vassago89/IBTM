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

    [Description("S1 carrier not seated")]
    SeatingPcbPlacementCarrier,

    [Description("S2 carrier not seated")]
    SeatingBoltFasteningCarrier,

    [Description("S3 carrier not seated")]
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
