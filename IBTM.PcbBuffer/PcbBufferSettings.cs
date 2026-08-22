using System;
using IBTM.Core;

namespace IBTM.PcbBuffer;

public sealed class PcbBufferSettings : Setting
{
    public double SupplyBoundary1 { get; set; }
    public double SupplyBoundary2 { get; set; }
    public AxisPos PlacementBoundary1 { get; set; } = new();
    public AxisPos PlacementBoundary2 { get; set; } = new();

    public bool ContainsSupply(double x)
    {
        var minimum = Math.Min(SupplyBoundary1, SupplyBoundary2);
        var maximum = Math.Max(SupplyBoundary1, SupplyBoundary2);
        return minimum < maximum && x >= minimum && x <= maximum;
    }

    public bool ContainsPlacement(double x, double y)
    {
        var minimumX = Math.Min(PlacementBoundary1.X, PlacementBoundary2.X);
        var maximumX = Math.Max(PlacementBoundary1.X, PlacementBoundary2.X);
        var minimumY = Math.Min(PlacementBoundary1.Y, PlacementBoundary2.Y);
        var maximumY = Math.Max(PlacementBoundary1.Y, PlacementBoundary2.Y);
        return minimumX < maximumX
            && minimumY < maximumY
            && x >= minimumX
            && x <= maximumX
            && y >= minimumY
            && y <= maximumY;
    }
}
