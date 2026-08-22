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

[JsonConverter(typeof(JsonStringEnumConverter<HousingSlot>))]
public enum HousingSlot
{
    [Description("Housing 1")]
    Housing1,

    [Description("Housing 2")]
    Housing2,
}
