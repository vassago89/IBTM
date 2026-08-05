using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplySettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public XzPos BufferPosition { get; set; } = new() { X = 10.0, Z = 8.0 };
}
