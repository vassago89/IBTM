namespace IBTM.Core;

public enum EquipmentUnit
{
    PcbSupply,
    PcbPlacement,
    BoltFastening,
    Inspection,
}

public enum PcbSlot
{
    Pcb1 = 1,
    Pcb2 = 2,
}

public enum ProcessStage
{
    Idle,
    Complete,
    Error,
    SupplyPcb,
    ReceiveCarrierJig,
    PositionPcbPlacementCarrierJig,
    AlignPcb,
    PlacePcb,
    TransferToBoltFastening,
    PositionBoltFasteningCarrierJig,
    TightenBolts,
    TransferToInspection,
    PositionInspectionCarrierJig,
    Inspect,
    StackNgCarrierJig,
    SendCarrierJig,
}

public enum StageStatus
{
    Idle,
    Running,
    Done,
    Error,
}

public enum InspectionResult
{
    Good,
    Ng,
    Missing,
}
