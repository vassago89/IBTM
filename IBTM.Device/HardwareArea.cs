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

    [Description("PCB Buffer")]
    PcbBuffer,

    [Description("PCB Placement Handler")]
    PcbPlacementHandler,

    [Description("PCB Placement Station")]
    PcbPlacementStation,

    [Description("Bolt Feeder")]
    BoltFeeder,

    [Description("Bolt Fastening Unit")]
    BoltFastening,

    [Description("Bolt Fastening Station")]
    BoltFasteningStation,

    [Description("Inspection Station")]
    InspectionStation,

    [Description("Inspection Gantry")]
    InspectionGantry,

    [Description("NG Shuttle")]
    NgShuttle,

    [Description("NG Conveyor")]
    NgConveyor,
}
