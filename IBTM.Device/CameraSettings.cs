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

public enum CameraRole
{
    [Description("Alignment")]
    Alignment,

    [Description("Inspection")]
    Inspection,
}

public abstract class CameraSettings : Setting
{
    public string DeviceId { get; set; } = string.Empty;
    public double ExposureMicroseconds { get; set; } = 500.0;
    public double Gain { get; set; }
    public int FrameTimeoutMilliseconds { get; set; } = 3_000;
}

public sealed class AlignmentCameraSettings : CameraSettings
{
}

public sealed class InspectionCameraSettings : CameraSettings
{
}
