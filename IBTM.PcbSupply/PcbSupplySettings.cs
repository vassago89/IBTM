using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplySettings
{
    public StationMotionSettings Motion { get; set; } = new();
    public double RotationX { get; set; } = 100.0;
    public XzPos HandoffPosition { get; set; } = new() { X = 100.0, Z = 25.0 };
}
