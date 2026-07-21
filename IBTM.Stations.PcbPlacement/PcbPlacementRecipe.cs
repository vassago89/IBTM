using IBTM.Core.Geometry;

namespace IBTM.Stations.PcbPlacement;

public sealed class PcbPlacementRecipe
{
    public AxisPos PcbPick1 { get; set; } = new() { X = 75.0, Y = 25.0, Z = 25.0 };
    public AxisPos PcbPlace1 { get; set; } = new() { X = 75.0, Y = 95.0, Z = 20.0 };
    public AxisPos PcbPick2 { get; set; } = new() { X = 130.0, Y = 25.0, Z = 25.0 };
    public AxisPos PcbPlace2 { get; set; } = new() { X = 130.0, Y = 95.0, Z = 20.0 };
}
