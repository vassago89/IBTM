using System.Windows;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;

namespace IBTM.UI;

public sealed class MachineDiagramMapper
{
    private readonly RecipeManager _recipes;
    private readonly PcbSupplySettings _supply;
    private readonly PcbPlacementHandlerSettings _placement;
    private static readonly Point s_supplyPcb1;
    private static readonly Point s_supplyPcb2;
    private static readonly Point s_supplyHandoff;
    private static readonly Point s_placementHandoff;
    private static readonly Point s_placementHeatSink1;
    private static readonly Point s_placementHeatSink2;

    static MachineDiagramMapper()
    {
        s_supplyPcb1 = MachineDiagramLayout.Offset(
            MachineDiagramLayout.SupplyPcb1Center,
            MachineDiagramLayout.SupplyToolCenter);
        s_supplyPcb2 = MachineDiagramLayout.Offset(
            MachineDiagramLayout.SupplyPcb2Center,
            MachineDiagramLayout.SupplyToolCenter);
        s_supplyHandoff = MachineDiagramLayout.Offset(
            MachineDiagramLayout.HandoffCenter,
            MachineDiagramLayout.SupplyToolCenter);
        s_placementHandoff = MachineDiagramLayout.Offset(
            MachineDiagramLayout.HandoffCenter,
            MachineDiagramLayout.PlacementToolCenter);
        s_placementHeatSink1 = MachineDiagramLayout.Offset(
            MachineDiagramLayout.PlacementHeatSink1,
            MachineDiagramLayout.PlacementToolCenter);
        s_placementHeatSink2 = MachineDiagramLayout.Offset(
            MachineDiagramLayout.PlacementHeatSink2,
            MachineDiagramLayout.PlacementToolCenter);
    }

    public MachineDiagramMapper(
        RecipeManager recipes,
        PcbSupplySettings supply,
        PcbPlacementHandlerSettings placement)
    {
        _recipes = recipes;
        _supply = supply;
        _placement = placement;
    }

    public bool IsSupplyDefined
    {
        get
        {
            if (_recipes.Current.PcbSupply.Pcb1PickPosition.Y is not { } pcb1Y
                || _recipes.Current.PcbSupply.Pcb2PickPosition.Y is not { } pcb2Y)
                return false;
            return MachineDiagramLayout.GetSide(
                new Point(_supply.HandoffPosition.X, _supply.HandoffPosition.Y),
                new Point(
                    _recipes.Current.PcbSupply.Pcb1PickPosition.X,
                    pcb1Y),
                new Point(
                    _recipes.Current.PcbSupply.Pcb2PickPosition.X,
                    pcb2Y)) != 0;
        }
    }

    public bool IsPlacementDefined
    {
        get
        {
            return MachineDiagramLayout.GetSide(
                new Point(
                    _placement.HandoffPosition.X,
                    _placement.HandoffPosition.Y),
                new Point(
                    _recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition.X,
                    _recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition.Y),
                new Point(
                    _recipes.Current.PcbPlacement.HeatSink2PcbPlacementPosition.X,
                    _recipes.Current.PcbPlacement.HeatSink2PcbPlacementPosition.Y)) != 0;
        }
    }

    public Point? GetSupplyPosition(MotionPosition current)
    {
        if (current is not { X: { } x, Y: { } y }
            || _recipes.Current.PcbSupply.Pcb1PickPosition.Y is not { } pcb1Y
            || _recipes.Current.PcbSupply.Pcb2PickPosition.Y is not { } pcb2Y)
            return null;
        return FromThreePoints(
            x,
            y,
            new Point(
                _recipes.Current.PcbSupply.Pcb1PickPosition.X,
                pcb1Y),
            new Point(
                _recipes.Current.PcbSupply.Pcb2PickPosition.X,
                pcb2Y),
            new Point(
                _supply.HandoffPosition.X,
                _supply.HandoffPosition.Y),
            s_supplyPcb1,
            s_supplyPcb2,
            s_supplyHandoff);
    }

    public Point? GetPlacementPosition(MotionPosition current)
    {
        if (current is not { X: { } x, Y: { } y })
            return null;
        return FromThreePoints(
            x,
            y,
            new Point(
                _placement.HandoffPosition.X,
                _placement.HandoffPosition.Y),
            new Point(
                _recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition.X,
                _recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition.Y),
            new Point(
                _recipes.Current.PcbPlacement.HeatSink2PcbPlacementPosition.X,
                _recipes.Current.PcbPlacement.HeatSink2PcbPlacementPosition.Y),
            s_placementHandoff,
            s_placementHeatSink1,
            s_placementHeatSink2);
    }

    private static Point FromThreePoints(
        double x,
        double y,
        Point source1,
        Point source2,
        Point source3,
        Point target1,
        Point target2,
        Point target3)
    {
        var area = MachineDiagramLayout.GetSide(source1, source2, source3);
        if (area == 0)
            return default;

        var first = MachineDiagramLayout.GetSide(new Point(x, y), source2, source3) / area;
        var second = MachineDiagramLayout.GetSide(new Point(x, y), source3, source1) / area;
        var third = 1 - first - second;
        return new Point(
            (first * target1.X) + (second * target2.X) + (third * target3.X),
            (first * target1.Y) + (second * target2.Y) + (third * target3.Y));
    }
}
