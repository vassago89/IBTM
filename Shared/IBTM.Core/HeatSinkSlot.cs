using System.ComponentModel;
using System.Text.Json.Serialization;

namespace IBTM.Core;

[JsonConverter(typeof(JsonStringEnumConverter<HeatSinkSlot>))]
public enum HeatSinkSlot
{
    [Description("Heat Sink 1")]
    HeatSink1,

    [Description("Heat Sink 2")]
    HeatSink2,
}
