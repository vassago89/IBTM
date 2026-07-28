using System.Text.Json.Serialization;

namespace IBTM.Device;

[JsonConverter(typeof(JsonStringEnumConverter<CameraDriver>))]
public enum CameraDriver
{
    Virtual,
    Hik,
}

public sealed class CameraSettings
{
    public string DeviceId { get; set; } = string.Empty;
    public double ExposureMicroseconds { get; set; } = 500.0;
    public double Gain { get; set; }
    public int FrameTimeoutMilliseconds { get; set; } = 3_000;
}
