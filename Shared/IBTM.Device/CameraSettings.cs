using IBTM.Core;

namespace IBTM.Device;

public sealed class InspectionCameraSettings : Setting
{
    public string DeviceId { get; set; } = string.Empty;
    public int FrameTimeoutMilliseconds { get; set; } = 3_000;
}
