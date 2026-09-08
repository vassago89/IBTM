using System.ComponentModel;
using System.Text.Json.Serialization;

namespace IBTM.Core;

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
    public FasteningHead Head { get; set; } = FasteningHead.Shooting;
    public double? X { get; set; }
    public double? Y { get; set; }
}
