using IBTM.Core;
using System.Text.Json.Serialization;

namespace IBTM.Inspection;

public sealed class CarrierImageTile
{
    public int Number { get; set; }
    // Data Matrix position. Bolt inspection XY belongs to BoltPoint.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AxisPosition? Center { get; set; }
    public PixelRegion? Region { get; set; }
    public int? BoltNumber { get; set; }
    public bool IsBarcode { get; set; }
    public HeatSinkSlot HeatSink { get; set; }
}
