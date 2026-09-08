using System.ComponentModel;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Device;

[JsonConverter(typeof(JsonStringEnumConverter<CameraDriver>))]
public enum CameraDriver
{
    [Description("Virtual")]
    Virtual,

    [Description("HIK")]
    Hik,
}

public sealed class InspectionCameraSettings : Setting
{
    public string DeviceId { get; set; } = string.Empty;
    public int FrameTimeoutMilliseconds { get; set; } = 3_000;
    public int LiveViewFramesPerSecond { get; set; } = 10;
}
