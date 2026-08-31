using System.ComponentModel;
using System.Text.Json.Serialization;

namespace IBTM.Core;

public enum MotionGroup
{
    [Description("Supply Handler")]
    PcbSupply,

    [Description("Placement Handler")]
    PcbPlacementHandler,

    [Description("Bolt Fastening")]
    BoltFastening,

    [Description("Inspection Gantry")]
    InspectionGantry,
}

[JsonConverter(typeof(JsonStringEnumConverter<HeatSinkSlot>))]
public enum HeatSinkSlot
{
    [Description("Heat Sink 1")]
    HeatSink1,

    [Description("Heat Sink 2")]
    HeatSink2,
}
