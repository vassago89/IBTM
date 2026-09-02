using System;
using IBTM.Core;

namespace IBTM.PcbBuffer;

public sealed class PcbBufferSettings : Setting
{
    public double SupplyBoundary1 { get; set; }
    public double SupplyBoundary2 { get; set; }
    public AxisPosition PlacementBoundary1 { get; set; } = new();
    public AxisPosition PlacementBoundary2 { get; set; } = new();

    public bool ContainsSupplyX(double x) =>
        x >= Math.Min(SupplyBoundary1, SupplyBoundary2)
        && x <= Math.Max(SupplyBoundary1, SupplyBoundary2);
}
