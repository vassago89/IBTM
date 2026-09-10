using System.ComponentModel;
using System.IO.Ports;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Device;

[JsonConverter(typeof(JsonStringEnumConverter<LightDriver>))]
public enum LightDriver
{
    [Description("Virtual")]
    Virtual,

    [Description("MOVS (Serial)")]
    Movs,
}

public sealed class LightingSettings : Setting
{
    // Retain the persisted key used by existing COM port settings.
    public string Connection { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 19_200;
    public int DataBits { get; set; } = 8;

    [JsonConverter(typeof(JsonStringEnumConverter<Parity>))]
    public Parity Parity { get; set; } = Parity.None;

    [JsonConverter(typeof(JsonStringEnumConverter<StopBits>))]
    public StopBits StopBits { get; set; } = StopBits.One;
    public int WriteTimeoutMilliseconds { get; set; } = 1_000;
    public int InspectionChannel { get; set; } = 2;
}
