using IBTM.Core;

namespace IBTM.Device;

public sealed class LightingSettings : Setting
{
    public string Connection { get; set; } = string.Empty;
    public int InspectionChannel { get; set; } = 2;
}
