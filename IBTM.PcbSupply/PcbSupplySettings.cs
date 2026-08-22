using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplySettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public double CarrierY { get; set; }
    public double OutsideX { get; set; }
    public AxisPos BufferHandoffPosition { get; set; } = new();
    public double BufferClearZ { get; set; }
}
