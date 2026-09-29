using System.ComponentModel;
using System.Text.Json.Serialization;

namespace IBTM.Device;

// Persisted IDs: never renumber or reuse; these are not hardware channel numbers.
[JsonConverter(typeof(SignalIdJsonConverter<MachineAxis>))]
public enum MachineAxis
{
    [Description("PCB Supply Handler X")]
    PcbSupplyX = 0,

    [Description("PCB Supply Handler Y")]
    PcbSupplyY = 1,

    [Description("PCB Supply Handler Z")]
    PcbSupplyZ = 2,

    [Description("PCB Placement Handler X")]
    PcbPlacementHandlerX = 3,

    [Description("PCB Placement Handler Y")]
    PcbPlacementHandlerY = 4,

    [Description("PCB Placement Handler Z")]
    PcbPlacementHandlerZ = 5,

    [Description("Bolt Fastening X")]
    BoltFasteningX = 6,

    [Description("Bolt Fastening Y")]
    BoltFasteningY = 7,

    [Description("Bolt Fastening Z")]
    BoltFasteningZ = 8,

    [Description("Inspection Gantry X")]
    InspectionGantryX = 9,

    [Description("Inspection Gantry Y")]
    InspectionGantryY = 10,
}
