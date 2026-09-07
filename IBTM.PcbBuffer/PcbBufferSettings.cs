using System;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbBuffer;

public sealed class PcbBufferSettings : Setting
{
    public double SupplyBoundary1 { get; set; }
    public double SupplyBoundary2 { get; set; }
    public AxisPosition PlacementBoundary1 { get; set; } = new();
    public AxisPosition PlacementBoundary2 { get; set; } = new();

    public TeachingPosition[] GetTeachingPositions() =>
    [
        new(TeachingTarget.SupplyBufferBoundary1, MotionGroup.PcbSupply, TeachMode.XOnly,
            () => new() { X = SupplyBoundary1 }, p => SupplyBoundary1 = p.X, this) { Staged = true },
        new(TeachingTarget.SupplyBufferBoundary2, MotionGroup.PcbSupply, TeachMode.XOnly,
            () => new() { X = SupplyBoundary2 }, p => SupplyBoundary2 = p.X, this) { Staged = true },
        new(TeachingTarget.PlacementBufferBoundary1, MotionGroup.PcbPlacementHandler, TeachMode.XYOnly,
            () => PlacementBoundary1, p => PlacementBoundary1 = p, this) { Staged = true },
        new(TeachingTarget.PlacementBufferBoundary2, MotionGroup.PcbPlacementHandler, TeachMode.XYOnly,
            () => PlacementBoundary2, p => PlacementBoundary2 = p, this) { Staged = true },
    ];

    public bool ContainsSupplyX(double x) =>
        x >= Math.Min(SupplyBoundary1, SupplyBoundary2)
        && x <= Math.Max(SupplyBoundary1, SupplyBoundary2);
}
