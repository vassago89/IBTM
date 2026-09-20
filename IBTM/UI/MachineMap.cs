using System;
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
    private readonly InspectionGantrySettings _inspection;
    private readonly NgCarrierTransferSettings _transfer;
    private static readonly (double X, double Y) SupplyPcb1;
    private static readonly (double X, double Y) SupplyPcb2;
    private static readonly (double X, double Y) SupplyHandoff;
    private static readonly (double X, double Y) PlacementHandoff;
    private static readonly (double X, double Y) PlacementHeatSink1;
    private static readonly (double X, double Y) PlacementHeatSink2;
    private static readonly (double X, double Y) ShootingUpperLeft;
    private static readonly (double X, double Y) ShootingLowerRight;
    private static readonly (double X, double Y) PickupUpperLeft;
    private static readonly (double X, double Y) PickupFeederOffset;

    static MachineMap()
    {
        SupplyPcb1 = MachinePlan.Offset(
            MachinePlan.SupplyPcb1Center,
            MachinePlan.SupplyToolCenter);
        SupplyPcb2 = MachinePlan.Offset(
            MachinePlan.SupplyPcb2Center,
            MachinePlan.SupplyToolCenter);
        SupplyHandoff = MachinePlan.Offset(
            MachinePlan.HandoffCenter,
            MachinePlan.SupplyToolCenter);
        PlacementHandoff = MachinePlan.Offset(
            MachinePlan.HandoffCenter,
            MachinePlan.PlacementToolCenter);
        PlacementHeatSink1 = MachinePlan.Offset(
            MachinePlan.PlacementHeatSink1,
            MachinePlan.PlacementToolCenter);
        PlacementHeatSink2 = MachinePlan.Offset(
            MachinePlan.PlacementHeatSink2,
            MachinePlan.PlacementToolCenter);
        ShootingUpperLeft = MachinePlan.Offset(
            MachinePlan.FasteningUpperLeft,
            MachinePlan.ShootingToolCenter);
        ShootingLowerRight = MachinePlan.Offset(
            MachinePlan.FasteningLowerRight,
            MachinePlan.ShootingToolCenter);
        PickupUpperLeft = MachinePlan.Offset(
            MachinePlan.FasteningUpperLeft,
            MachinePlan.PickupToolCenter);
        PickupFeederOffset = (
            -MachinePlan.PickupFeederWidth / 2,
            -MachinePlan.PickupFeederHeight / 2);
    }

    public MachineMap(
        RecipeManager recipes,
        PcbSupplySettings supply,
        PcbPlacementHandlerSettings placement,
        BoltFasteningSettings fastening,
        CarrierReferenceSettings carrier,
        InspectionGantrySettings inspection,
        NgCarrierTransferSettings transfer)
    {
        _recipes = recipes;
        _supply = supply;
        _placement = placement;
        _fastening = fastening;
        _carrier = carrier;
        _inspection = inspection;
        _transfer = transfer;
    }

    public bool SupplyDefined
    {
        get
        {
            return MachinePlan.GetSide(
                (_supply.HandoffPosition.X, _supply.HandoffPosition.Y),
                (
                    _recipes.Current.PcbSupply.Pcb1PickPosition.X,
                    _supply.CarrierY),
                (
                    _recipes.Current.PcbSupply.Pcb2PickPosition.X,
                    _supply.CarrierY)) != 0;
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
        if (current is not { X: { } x, Y: { } y })
            return null;
        return FromThreePoints(
            x,
            y,
            (
                _recipes.Current.PcbSupply.Pcb1PickPosition.X,
                _supply.CarrierY),
            (
                _recipes.Current.PcbSupply.Pcb2PickPosition.X,
                _supply.CarrierY),
            (
                _supply.HandoffPosition.X,
                _supply.HandoffPosition.Y),
            SupplyPcb1,
            SupplyPcb2,
            SupplyHandoff);
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
            PlacementHandoff,
            PlacementHeatSink1,
            PlacementHeatSink2);
    }

    public (double X, double Y)? GetFasteningPosition(MotionPosition current)
    {
        return current is { X: { } x, Y: { } y } ? MapFastening(x, y) : null;
    }

    public (double X, double Y) PickupFeederPosition
    {
        get
        {
            var point = _fastening.PickupPosition;
            var mapped = MapFastening(point.X, point.Y);
            return (
                mapped.X + MachinePlan.PickupToolCenter.X + PickupFeederOffset.X,
                mapped.Y + MachinePlan.PickupToolCenter.Y + PickupFeederOffset.Y);
        }
    }

    public (double X, double Y) GetFasteningTargetPosition(BoltPoint bolt)
    {
        var target = _fastening.GetBoltPosition(bolt, _carrier);
        var mapped = MapFastening(target.X, target.Y);
        var tool = bolt.Head == FasteningHead.Pickup
            ? MachinePlan.PickupToolCenter
            : MachinePlan.ShootingToolCenter;
        return (
            mapped.X + tool.X - MachinePlan.FasteningContentOrigin.X,
            mapped.Y + tool.Y - MachinePlan.FasteningContentOrigin.Y);
    }

    public (double X, double Y)? GetInspectionPosition(MotionPosition current)
    {
        return current is { X: { } x, Y: { } y } ? MapInspection(x, y) : null;
    }

    public (double X, double Y) GetInspectionTargetPosition(BoltPoint bolt)
    {
        var target = _inspection.GetBoltPosition(bolt, _carrier);
        var mapped = MapInspection(target.X, target.Y);
        return (
            mapped.X + MachinePlan.CameraCenter.X - MachinePlan.InspectionContentOrigin.X,
            mapped.Y + MachinePlan.CameraCenter.Y - MachinePlan.InspectionContentOrigin.Y);
    }

    private (double X, double Y) MapFastening(double x, double y)
    {
        switch (true)
        {
            case true when !FasteningDefined:
                return default;
            case true when _fastening.ShootingHead.UpperLeftLocatingPin is { } first
                && _fastening.ShootingHead.LowerRightLocatingPin is { } second
                && _fastening.PickupHead.UpperLeftLocatingPin is { } pickup
                && MachinePlan.GetSide((pickup.X, pickup.Y), (first.X, first.Y), (second.X, second.Y)) != 0:
                return FromThreePoints(
                    x,
                    y,
                    (first.X, first.Y),
                    (second.X, second.Y),
                    (pickup.X, pickup.Y),
                    ShootingUpperLeft,
                    ShootingLowerRight,
                    PickupUpperLeft);
        }

        var shooting = HasPins(_fastening.ShootingHead);
        var head = shooting ? _fastening.ShootingHead : _fastening.PickupHead;
        var tool = shooting ? MachinePlan.ShootingToolCenter : MachinePlan.PickupToolCenter;
        return FromTwoPoints(
            x,
            y,
            (head.UpperLeftLocatingPin!.X, head.UpperLeftLocatingPin.Y),
            (
                head.LowerRightLocatingPin!.X,
                head.LowerRightLocatingPin.Y),
            MachinePlan.Offset(MachinePlan.FasteningUpperLeft, tool),
            MachinePlan.Offset(MachinePlan.FasteningLowerRight, tool));
    }

    private (double X, double Y) MapInspection(double x, double y)
    {
        if (!InspectionDefined)
            return default;
        var upperLeft = _carrier.UpperLeftLocatingPin!;
        var lowerRight = _carrier.LowerRightLocatingPin!;
        var first = (upperLeft.X, upperLeft.Y);
        var second = (lowerRight.X, lowerRight.Y);
        var pickup = _transfer.GetCarrierPickupPosition();
        var shuttle = _transfer.ShuttlePlacePosition;
        var pickupSide = pickup is null ? 0 : MachinePlan.GetSide((pickup.X, pickup.Y), first, second);
        var shuttleSide = MachinePlan.GetSide((shuttle.X, shuttle.Y), first, second);
        var cameraUpperLeft = MachinePlan.Offset(
            MachinePlan.InspectionUpperLeft,
            MachinePlan.CameraCenter);
        var cameraLowerRight = MachinePlan.Offset(
            MachinePlan.InspectionLowerRight,
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
