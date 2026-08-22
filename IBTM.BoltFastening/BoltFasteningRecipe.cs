using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.BoltFastening;

[JsonConverter(typeof(JsonStringEnumConverter<FasteningHead>))]
public enum FasteningHead
{
    [Description("Head 2 Shooting")]
    Shooting,

    [Description("Head 1 Pickup")]
    Pickup,
}

public sealed class BoltPoint
{
    public int Number { get; set; }
    public HousingSlot Housing { get; set; } = HousingSlot.Housing1;
    public FasteningHead Head { get; set; } = FasteningHead.Shooting;
    public double? X { get; set; }
    public double? Y { get; set; }
    public double Z { get; set; }
    public ushort Preset { get; set; } = 1;
}

public sealed class BoltFasteningRecipe
{
    public List<BoltPoint> BoltPoints { get; set; } = [];
}
