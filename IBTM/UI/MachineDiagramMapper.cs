using System;
using System.Windows;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;

namespace IBTM.UI;

public sealed class MachineDiagramMapper
{
    private readonly RecipeManager _recipes;
    private readonly PcbSupplySettings _supply;
    private readonly PcbPlacementHandlerSettings _placement;
    private readonly BoltFasteningSettings _fastening;
    private readonly CarrierReferenceSettings _carrier;
    private readonly NgCarrierTransferSettings _transfer;
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
        PcbPlacementHandlerSettings placement,
        BoltFasteningSettings fastening,
        CarrierReferenceSettings carrier,
        NgCarrierTransferSettings transfer)
    {
        _recipes = recipes;
        _supply = supply;
        _placement = placement;
        _fastening = fastening;
        _carrier = carrier;
        _transfer = transfer;
    }

    public bool SupplyDefined
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

    public bool PlacementDefined
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

    public bool FasteningDefined
    {
        get
        {
            return _carrier.IsDefined
                && (CarrierCoordinates.IsDefined(
                        _fastening.ShootingHead.UpperLeftLocatingPin, _fastening.ShootingHead.LowerRightLocatingPin)
                    || CarrierCoordinates.IsDefined(
                        _fastening.PickupHead.UpperLeftLocatingPin, _fastening.PickupHead.LowerRightLocatingPin));
        }
    }

    public bool InspectionDefined => _carrier.IsDefined;

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

    public Point? GetFasteningPosition(MotionPosition current, FasteningHead head)
    {
        return current is { X: { } x, Y: { } y }
            ? MapFastening(x, y, head, head == FasteningHead.Pickup
                ? MachineDiagramLayout.PickupToolCenter : MachineDiagramLayout.ShootingToolCenter)
            : null;
    }

    public Point? GetFasteningTargetPosition(BoltPoint bolt)
    {
        return bolt.IsFasteningPositionDefined
            ? MapFastening(
                bolt.FasteningX!.Value, bolt.FasteningY!.Value, bolt.Head, MachineDiagramLayout.FasteningContentOrigin)
            : null;
    }

    public Point? GetInspectionCameraPosition(MotionPosition current)
    {
        // Camera feedback and taught bolt positions must use the same carrier projection.
        return InspectionDefined && current is { X: { } x, Y: { } y }
            ? MachineDiagramLayout.Offset(
                MapCarrier(x, y, MachineDiagramLayout.InspectionUpperLeft, MachineDiagramLayout.InspectionLowerRight),
                MachineDiagramLayout.CameraCenter)
            : null;
    }

    public Point? GetNgPickupPosition(MotionPosition current)
    {
        if (!InspectionDefined || current is not { X: { } x, Y: { } y })
            return null;
        var waiting = _transfer.WaitingPosition;
        var shuttle = _transfer.ShuttlePlacePosition;
        if (waiting is null || shuttle.Y == waiting.Y)
            return GetInspectionCameraPosition(current);

        // Centre the waiting position; retain the taught pickup's offset from it.
        // Use one map throughout the transfer so straight moves remain straight.
        var progress = (y - waiting.Y) / (shuttle.Y - waiting.Y);
        var routeX = waiting.X + (shuttle.X - waiting.X) * progress;
        var position = MapCarrier(x, waiting.Y,
            MachineDiagramLayout.InspectionUpperLeft, MachineDiagramLayout.InspectionLowerRight);
        var route = MapCarrier(routeX, waiting.Y,
            MachineDiagramLayout.InspectionUpperLeft, MachineDiagramLayout.InspectionLowerRight);
        var origin = MachineDiagramLayout.Offset(
            MachineDiagramLayout.InspectionCarrierCenter, MachineDiagramLayout.NgPickerCenter);
        // Retain live X deviation from the taught route, including separate-axis moves.
        return new Point(
            origin.X + position.X - route.X,
            origin.Y + (MachineDiagramLayout.NgShuttleCenter.Y - MachineDiagramLayout.InspectionCarrierCenter.Y) * progress);
    }

    public Point? GetInspectionTargetPosition(BoltPoint bolt)
    {
        if (!InspectionDefined || _recipes.Current.FindInspectionImage(bolt.HeatSink, bolt.Id) is null
            || bolt.InspectionPosition is not { } center)
            return null;
        var mapped = MapCarrier(center.X, center.Y, MachineDiagramLayout.InspectionUpperLeft, MachineDiagramLayout.InspectionLowerRight);
        return MachineDiagramLayout.Offset(mapped, MachineDiagramLayout.InspectionContentOrigin);
    }

    private Point? MapFastening(
        double x, double y, FasteningHead head, Point origin)
    {
        var settings = _fastening.GetHead(head);
        if (!_carrier.IsDefined
            || !CarrierCoordinates.IsDefined(settings.UpperLeftLocatingPin, settings.LowerRightLocatingPin))
            return null;
        // Project this head's stored or live machine XY back onto the carrier drawing.
        var carrier = CarrierCoordinates.ToMachine(new() { X = x, Y = y },
            settings.UpperLeftLocatingPin!, settings.LowerRightLocatingPin!,
            _carrier.UpperLeftLocatingPin!, _carrier.LowerRightLocatingPin!);
        var mapped = MapCarrier(carrier.X, carrier.Y, MachineDiagramLayout.FasteningUpperLeft, MachineDiagramLayout.FasteningLowerRight);
        return MachineDiagramLayout.Offset(mapped, origin);
    }

    private Point MapCarrier(double x, double y,
        Point upperLeft, Point lowerRight)
    {
        var first = _carrier.UpperLeftLocatingPin!;
        var second = _carrier.LowerRightLocatingPin!;
        // Locating pins are reference positions, not the outside edges of the carrier.
        // Fit the whole taught carrier, independently of live position, active head and presence sensors.
        var minX = Math.Min(first.X, second.X);
        var maxX = Math.Max(first.X, second.X);
        var minY = Math.Min(first.Y, second.Y);
        var maxY = Math.Max(first.Y, second.Y);
        foreach (var bolt in _recipes.Current.Pcb.BoltPoints)
        {
            if (bolt.InspectionPosition is not { } position)
                continue;
            minX = Math.Min(minX, position.X);
            maxX = Math.Max(maxX, position.X);
            minY = Math.Min(minY, position.Y);
            maxY = Math.Max(maxY, position.Y);
        }
        foreach (var fov in _recipes.Current.CarrierImages)
        {
            if (!fov.IsBarcode || fov.Center is not { } position
                || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
                continue;
            minX = Math.Min(minX, position.X);
            maxX = Math.Max(maxX, position.X);
            minY = Math.Min(minY, position.Y);
            maxY = Math.Max(maxY, position.Y);
        }
        // Keep the carrier centre fixed so adding an outer bolt does not shift the HS1/HS2 boundary.
        var centerX = (first.X + second.X) / 2;
        var centerY = (first.Y + second.Y) / 2;
        var halfWidth = Math.Max(centerX - minX, maxX - centerX);
        var halfHeight = Math.Max(centerY - minY, maxY - centerY);
        var sourceFirst = new Point(centerX + (first.X <= second.X ? -halfWidth : halfWidth),
            centerY + (first.Y <= second.Y ? -halfHeight : halfHeight));
        var sourceSecond = new Point(centerX + (first.X <= second.X ? halfWidth : -halfWidth),
            centerY + (first.Y <= second.Y ? halfHeight : -halfHeight));
        return FromTwoPoints(x, y, sourceFirst, sourceSecond, upperLeft, lowerRight);
    }

    private static Point FromTwoPoints(
        double x,
        double y,
        Point first,
        Point second,
        Point targetFirst,
        Point targetSecond)
    {
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        var tx = targetSecond.X - targetFirst.X;
        var ty = targetSecond.Y - targetFirst.Y;
        // Diagonal pins define separate X/Y scales. An axis-aligned pair uses a similarity transform.
        if (dx != 0 && dy != 0)
            return new Point(targetFirst.X + (x - first.X) * tx / dx, targetFirst.Y + (y - first.Y) * ty / dy);

        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
            return default;
        var a = (tx * dx + ty * dy) / lengthSquared;
        var b = (ty * dx - tx * dy) / lengthSquared;
        return new Point(
            targetFirst.X + a * (x - first.X) - b * (y - first.Y),
            targetFirst.Y + b * (x - first.X) + a * (y - first.Y));
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
