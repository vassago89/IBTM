using System.ComponentModel;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.AlphaMotion;

[JsonConverter(typeof(JsonStringEnumConverter<AlphaMotionCommunicationSpeed>))]
public enum AlphaMotionCommunicationSpeed
{
    [Description("2.5 Mbps")]
    Mbps2_5 = 0,

    [Description("5 Mbps")]
    Mbps5 = 1,

    [Description("10 Mbps")]
    Mbps10 = 2,

    [Description("20 Mbps")]
    Mbps20 = 3,
}

public sealed class AlphaMotionSettings : Setting
{
    public int ControllerNumber { get; set; }
    public int StationNumber { get; set; } = 1;
    public AlphaMotionCommunicationSpeed CommunicationSpeed { get; set; } =
        AlphaMotionCommunicationSpeed.Mbps20;
}
