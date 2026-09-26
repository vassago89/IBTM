using System;
using System.Linq;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;

namespace IBTM.UI;

public sealed class MachineMap
{
    private readonly RecipeManager _recipes;
    private readonly PcbSupplySettings _supply;
    private readonly PcbPlacementHandlerSettings _placement;
    private readonly BoltFasteningSettings _fastening;
    private readonly CarrierReferenceSettings _carrier;
    private readonly NgCarrierTransferSettings _transfer;
    private static readonly (double X, double Y) s_supplyPcb1;
    private static readonly (double X, double Y) s_supplyPcb2;
    private static readonly (double X, double Y) s_supplyHandoff;
    private static readonly (double X, double Y) s_placementHandoff;
    private static readonly (double X, double Y) s_placementHeatSink1;
    private static readonly (double X, double Y) s_placementHeatSink2;
    private static readonly (double X, double Y) s_pickupFeederOffset;

    static MachineMap()
    {
        s_supplyPcb1 = MachinePlan.Offset(
            MachinePlan.SupplyPcb1Center,
            MachinePlan.SupplyToolCenter);
        s_supplyPcb2 = MachinePlan.Offset(
            MachinePlan.SupplyPcb2Center,
            MachinePlan.SupplyToolCenter);
        s_supplyHandoff = MachinePlan.Offset(
            MachinePlan.HandoffCenter,
            MachinePlan.SupplyToolCenter);
        s_placementHandoff = MachinePlan.Offset(
            MachinePlan.HandoffCenter,
            MachinePlan.PlacementToolCenter);
        s_placementHeatSink1 = MachinePlan.Offset(
            MachinePlan.PlacementHeatSink1,
            MachinePlan.PlacementToolCenter);
        s_placementHeatSink2 = MachinePlan.Offset(
            MachinePlan.PlacementHeatSink2,
            MachinePlan.PlacementToolCenter);
        s_pickupFeederOffset = (
            -MachinePlan.PickupFeederWidth / 2,
            -MachinePlan.PickupFeederHeight / 2);
    }

    public MachineMap(
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
            return MachinePlan.GetSide(
                (_supply.HandoffPosition.X, _supply.HandoffPosition.Y),
                (
                    _recipes.Current.PcbSupply.Pcb1PickPosition.X,
                    pcb1Y),
                (
                    _recipes.Current.PcbSupply.Pcb2PickPosition.X,
                    pcb2Y)) != 0;
        }
    }

    public bool PlacementDefined
    {
        get
        {
            return MachinePlan.GetSide(
                (
                    _placement.HandoffPosition.X,
                    _placement.HandoffPosition.Y),
                (
                    _recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition.X,
                    _recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition.Y),
                (
                    _recipes.Current.PcbPlacement.HeatSink2PcbPlacementPosition.X,
                    _recipes.Current.PcbPlacement.HeatSink2PcbPlacementPosition.Y)) != 0;
        }
    }

    public bool FasteningDefined
    {
        get
        {
            return _carrier.IsDefined
                && (HasPins(_fastening.ShootingHead) || HasPins(_fastening.PickupHead));
        }
    }

    public bool InspectionDefined => _carrier.IsDefined;

    private static bool HasPins(BoltHeadSettings head)
    {
        return CarrierCoordinates.IsDefined(head.UpperLeftLocatingPin, head.LowerRightLocatingPin);
    }

    public (double X, double Y)? GetSupplyPosition(MotionPosition current)
    {
        if (current is not { X: { } x, Y: { } y }
            || _recipes.Current.PcbSupply.Pcb1PickPosition.Y is not { } pcb1Y
            || _recipes.Current.PcbSupply.Pcb2PickPosition.Y is not { } pcb2Y)
            return null;
        return FromThreePoints(
            x,
            y,
            (
                _recipes.Current.PcbSupply.Pcb1PickPosition.X,
                pcb1Y),
            (
                _recipes.Current.PcbSupply.Pcb2PickPosition.X,
                pcb2Y),
            (
                _supply.HandoffPosition.X,
                _supply.HandoffPosition.Y),
            s_supplyPcb1,
            s_supplyPcb2,
            s_supplyHandoff);
    }

    public (double X, double Y)? GetPlacementPosition(MotionPosition current)
    {
        if (current is not { X: { } x, Y: { } y })
            return null;
        return FromThreePoints(
            x,
            y,
            (
                _placement.HandoffPosition.X,
                _placement.HandoffPosition.Y),
            (
                _recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition.X,
                _recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition.Y),
            (
                _recipes.Current.PcbPlacement.HeatSink2PcbPlacementPosition.X,
                _recipes.Current.PcbPlacement.HeatSink2PcbPlacementPosition.Y),
            s_placementHandoff,
            s_placementHeatSink1,
            s_placementHeatSink2);
    }

    public (double X, double Y)? GetFasteningPosition(MotionPosition current, FasteningHead head)
    {
        return current is { X: { } x, Y: { } y }
            ? MapFastening(x, y, head, head == FasteningHead.Pickup
                ? MachinePlan.PickupToolCenter : MachinePlan.ShootingToolCenter)
            : null;
    }

    public (double X, double Y)? PickupFeederPosition
    {
        get
        {
            var point = _fastening.PickupPosition;
            if (MapFastening(point.X, point.Y, FasteningHead.Pickup, MachinePlan.PickupToolCenter) is not { } mapped)
                return null;
            return (
                mapped.X + MachinePlan.PickupToolCenter.X + s_pickupFeederOffset.X,
                mapped.Y + MachinePlan.PickupToolCenter.Y + s_pickupFeederOffset.Y);
        }
    }

    public (double X, double Y)? GetFasteningTargetPosition(BoltPoint bolt)
    {
        return bolt is { FasteningX: { } x, FasteningY: { } y }
            ? MapFastening(x, y, bolt.Head, MachinePlan.FasteningContentOrigin)
            : null;
    }

    public (double X, double Y)? GetInspectionPosition(MotionPosition current)
    {
        return InspectionDefined && current is { X: { } x, Y: { } y }
            ? MachinePlan.Offset(MapCarrier(x, y, MachinePlan.InspectionUpperLeft, MachinePlan.InspectionLowerRight),
                MachinePlan.CameraCenter)
            : null;
    }

    public (double X, double Y)? GetNgPickupPosition(MotionPosition current)
    {
        if (!InspectionDefined || current is not { X: { } x, Y: { } y })
            return null;
        var upperLeft = _carrier.UpperLeftLocatingPin!;
        var lowerRight = _carrier.LowerRightLocatingPin!;
        var first = (upperLeft.X, upperLeft.Y);
        var second = (lowerRight.X, lowerRight.Y);
        var pickup = _transfer.CarrierPickupPosition;
        var shuttle = _transfer.ShuttlePlacePosition;
        var pickupSide = pickup is null ? 0 : MachinePlan.GetSide((pickup.X, pickup.Y), first, second);
        var shuttleSide = MachinePlan.GetSide((shuttle.X, shuttle.Y), first, second);
        var cameraUpperLeft = MachinePlan.Offset(
            MapCarrier(first.X, first.Y, MachinePlan.InspectionUpperLeft, MachinePlan.InspectionLowerRight),
            MachinePlan.CameraCenter);
        var cameraLowerRight = MachinePlan.Offset(
            MapCarrier(second.X, second.Y, MachinePlan.InspectionUpperLeft, MachinePlan.InspectionLowerRight),
            MachinePlan.CameraCenter);

        if (pickup is not null && pickupSide * shuttleSide < 0)
        {
            var towardPickup = MachinePlan.GetSide((x, y), first, second) * pickupSide >= 0;
            return FromThreePoints(
                x,
                y,
                first,
                second,
                towardPickup ? (pickup.X, pickup.Y) : (
                    shuttle.X,
                    shuttle.Y),
                cameraUpperLeft,
                cameraLowerRight,
                MachinePlan.Offset(
                    towardPickup ? MachinePlan.InspectionCarrierCenter : MachinePlan.NgShuttleCenter,
                    MachinePlan.NgPickerCenter));
        }

        return FromTwoPoints(x, y, first, second, cameraUpperLeft, cameraLowerRight);
    }

    public (double X, double Y)? GetInspectionTargetPosition(BoltPoint bolt)
    {
        var fovCount = _recipes.Current.CarrierImages.Count(fov => !fov.IsBarcode
            && fov.HeatSink == bolt.HeatSink && fov.BoltNumber == bolt.Number);
        if (!InspectionDefined || fovCount != 1 || bolt.InspectionPosition is not { } center)
            return null;
        var mapped = MapCarrier(center.X, center.Y, MachinePlan.InspectionUpperLeft, MachinePlan.InspectionLowerRight);
        return MachinePlan.Offset(mapped, MachinePlan.InspectionContentOrigin);
    }

    private (double X, double Y)? MapFastening(
        double x, double y, FasteningHead head, (double X, double Y) origin)
    {
        var settings = _fastening.GetHead(head);
        if (!_carrier.IsDefined || !HasPins(settings))
            return null;
        // Project this head's stored or live machine XY back onto the carrier drawing.
        var carrier = CarrierCoordinates.ToMachine(new() { X = x, Y = y },
            settings.UpperLeftLocatingPin!, settings.LowerRightLocatingPin!,
            _carrier.UpperLeftLocatingPin!, _carrier.LowerRightLocatingPin!);
        var mapped = MapCarrier(carrier.X, carrier.Y, MachinePlan.FasteningUpperLeft, MachinePlan.FasteningLowerRight);
        return MachinePlan.Offset(mapped, origin);
    }

    private (double X, double Y) MapCarrier(double x, double y,
        (double X, double Y) upperLeft, (double X, double Y) lowerRight)
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
            if (bolt is not { X: { } boltX, Y: { } boltY })
                continue;
            minX = Math.Min(minX, boltX);
            maxX = Math.Max(maxX, boltX);
            minY = Math.Min(minY, boltY);
            maxY = Math.Max(maxY, boltY);
        }
        foreach (var fov in _recipes.Current.CarrierImages)
        {
            if (!fov.IsBarcode || fov.Center is null)
                continue;
            minX = Math.Min(minX, fov.Center.X);
            maxX = Math.Max(maxX, fov.Center.X);
            minY = Math.Min(minY, fov.Center.Y);
            maxY = Math.Max(maxY, fov.Center.Y);
        }
        // Keep the carrier centre fixed so adding an outer bolt does not shift the HS1/HS2 boundary.
        var centerX = (first.X + second.X) / 2;
        var centerY = (first.Y + second.Y) / 2;
        var halfWidth = Math.Max(centerX - minX, maxX - centerX);
        var halfHeight = Math.Max(centerY - minY, maxY - centerY);
        var sourceFirst = (centerX + (first.X <= second.X ? -halfWidth : halfWidth),
            centerY + (first.Y <= second.Y ? -halfHeight : halfHeight));
        var sourceSecond = (centerX + (first.X <= second.X ? halfWidth : -halfWidth),
            centerY + (first.Y <= second.Y ? halfHeight : -halfHeight));
        return FromTwoPoints(x, y, sourceFirst, sourceSecond, upperLeft, lowerRight);
    }

    private static (double X, double Y) FromTwoPoints(
        double x,
        double y,
        (double X, double Y) first,
        (double X, double Y) second,
        (double X, double Y) targetFirst,
        (double X, double Y) targetSecond)
    {
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        var tx = targetSecond.X - targetFirst.X;
        var ty = targetSecond.Y - targetFirst.Y;
        // Diagonal pins define separate X/Y scales. An axis-aligned pair uses a similarity transform.
        if (dx != 0 && dy != 0)
            return (targetFirst.X + (x - first.X) * tx / dx, targetFirst.Y + (y - first.Y) * ty / dy);

        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
            return default;
        var a = (tx * dx + ty * dy) / lengthSquared;
        var b = (ty * dx - tx * dy) / lengthSquared;
        return (
            targetFirst.X + a * (x - first.X) - b * (y - first.Y),
            targetFirst.Y + b * (x - first.X) + a * (y - first.Y));
    }

    private static (double X, double Y) FromThreePoints(
        double x,
        double y,
        (double X, double Y) source1,
        (double X, double Y) source2,
        (double X, double Y) source3,
        (double X, double Y) target1,
        (double X, double Y) target2,
        (double X, double Y) target3)
    {
        var area = MachinePlan.GetSide(source1, source2, source3);
        if (area == 0)
            return default;

        var first = MachinePlan.GetSide((x, y), source2, source3) / area;
        var second = MachinePlan.GetSide((x, y), source3, source1) / area;
        var third = 1 - first - second;
        return (
            (first * target1.X) + (second * target2.X) + (third * target3.X),
            (first * target1.Y) + (second * target2.Y) + (third * target3.Y));
    }
}
