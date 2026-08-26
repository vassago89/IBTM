using IBTM.Core;

namespace IBTM.PcbBuffer;

public sealed class PcbBufferSettings : Setting
{
    public double SupplyBoundary1 { get; set; }
    public double SupplyBoundary2 { get; set; }
    public AxisPos PlacementBoundary1 { get; set; } = new();
    public AxisPos PlacementBoundary2 { get; set; } = new();
}
