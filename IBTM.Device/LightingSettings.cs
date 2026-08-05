using IBTM.Core;

namespace IBTM.Device;

public sealed class LightingSettings : Setting
{
    public string Connection { get; set; } = string.Empty;
    public int AlignmentChannel { get; set; } = 1;
    public int AlignmentLevel { get; set; } = 255;
    public int InspectionChannel { get; set; } = 2;
    public int InspectionLevel { get; set; } = 255;
}
