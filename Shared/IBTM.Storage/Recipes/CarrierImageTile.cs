using IBTM.Core;

namespace IBTM.Inspection;

public sealed class CarrierImageTile
{
    public CarrierImageTile()
    {
        Center = new();
    }

    public int Number { get; set; }
    public AxisPosition Center { get; set; }
    public PixelRegion? Region { get; set; }
    public int? BoltNumber { get; set; }
    public bool IsBarcode { get; set; }
    public HeatSinkSlot HeatSink { get; set; }
}
