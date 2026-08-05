using IBTM.Core;

namespace IBTM.PcbBuffer;

public sealed class PcbBufferSettings : Setting
{
    public double SupplyMinimumX { get; set; } = 5.0;
    public double SupplyMaximumX { get; set; } = 50.0;
    public double PlacementMinimumX { get; set; } = 5.0;
    public double PlacementMaximumX { get; set; } = 30.0;
    public double PlacementMinimumY { get; set; } = 5.0;
    public double PlacementMaximumY { get; set; } = 12.0;

    public bool IsConfigured =>
        SupplyMinimumX < SupplyMaximumX
        && PlacementMinimumX < PlacementMaximumX
        && PlacementMinimumY < PlacementMaximumY;

    public bool ContainsSupply(double x) =>
        x >= SupplyMinimumX
        && x <= SupplyMaximumX;

    public bool ContainsPlacement(double x, double y) =>
        x >= PlacementMinimumX
        && x <= PlacementMaximumX
        && y >= PlacementMinimumY
        && y <= PlacementMaximumY;
}
