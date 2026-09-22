using System.ComponentModel;

namespace IBTM.Device;

public enum HardwareArea
{
    [Description("Machine")]
    Machine,

    [Description("Main Conveyor")]
    MainConveyor,

    [Description("PCB Supply")]
    PcbSupply,

    [Description("PCB Placement Handler")]
    PcbPlacementHandler = 4,

    [Description("PCB Placement Station")]
    PcbPlacementStation,

    [Description("Bolt Feeder")]
    BoltFeeder,

    [Description("Bolt Fastening Unit")]
    BoltFastening,

    [Description("Bolt Fastening Station")]
    BoltFasteningStation,

    [Description("Inspection S3 Support")]
    InspectionStation,

    [Description("Inspection Station")]
    InspectionGantry,

    [Description("NG Carrier Transfer")]
    NgCarrierTransfer,

    [Description("NG Shuttle")]
    NgShuttle,

    [Description("NG Conveyor")]
    NgConveyor,
}
