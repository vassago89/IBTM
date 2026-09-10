using IBTM.Core;

namespace IBTM.Inspection;

public sealed class CarrierImageTile
{
    public int Number { get; set; }
    public AxisPosition Center { get; set; } = new();
    public PixelRegion? Region { get; set; }
    public int? BoltNumber { get; set; }
    public HeatSinkSlot HeatSink { get; set; }
}
