using System;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM.UI;

public sealed class MachineMap(
    Recipe recipe,
    PcbSupplySettings supply,
    PcbPlacementHandlerSettings placement,
    BoltFasteningSettings fastening,
    CarrierReferenceSettings carrier,
    InspectionGantrySettings inspection,
    NgCarrierTransferSettings transfer)
{
    private static readonly (double X, double Y) SupplyPcb1 = MachinePlan.Offset(MachinePlan.SupplyPcb1Center, MachinePlan.SupplyToolCenter);
    private static readonly (double X, double Y) SupplyPcb2 = MachinePlan.Offset(MachinePlan.SupplyPcb2Center, MachinePlan.SupplyToolCenter);
    private static readonly (double X, double Y) SupplyBuffer = MachinePlan.Offset(MachinePlan.BufferCenter, MachinePlan.SupplyToolCenter);
    private static readonly (double X, double Y) PlacementBuffer = MachinePlan.Offset(MachinePlan.BufferCenter, MachinePlan.PlacementToolCenter);
    private static readonly (double X, double Y) PlacementHeatSink1 = MachinePlan.Offset(MachinePlan.PlacementHeatSink1, MachinePlan.PlacementToolCenter);
    private static readonly (double X, double Y) PlacementHeatSink2 = MachinePlan.Offset(MachinePlan.PlacementHeatSink2, MachinePlan.PlacementToolCenter);
    private static readonly (double X, double Y) ShootingUpperLeft = MachinePlan.Offset(MachinePlan.FasteningUpperLeft, MachinePlan.ShootingToolCenter);
    private static readonly (double X, double Y) ShootingLowerRight = MachinePlan.Offset(MachinePlan.FasteningLowerRight, MachinePlan.ShootingToolCenter);
    private static readonly (double X, double Y) PickupUpperLeft = MachinePlan.Offset(MachinePlan.FasteningUpperLeft, MachinePlan.PickupToolCenter);
    private static readonly (double X, double Y) PickupToolOffset = MachinePlan.PickupToolCenter;
    private static readonly (double X, double Y) ShootingToolOffset = MachinePlan.ShootingToolCenter;
    private static readonly (double X, double Y) BoltTargetOrigin = MachinePlan.FasteningContentOrigin;
    private static readonly (double X, double Y) PickupFeederOffset = (-MachinePlan.PickupFeederWidth / 2, -MachinePlan.PickupFeederHeight / 2);

    public bool SupplyDefined => MachinePlan.Side(
        (supply.BufferHandoffPosition.X, supply.BufferHandoffPosition.Y),
        (recipe.PcbSupply.Pcb1PickPosition.X, supply.CarrierY),
        (recipe.PcbSupply.Pcb2PickPosition.X, supply.CarrierY)) != 0;

    public bool PlacementDefined => MachinePlan.Side(
        (placement.BufferHandoffPosition.X, placement.BufferHandoffPosition.Y),
        (recipe.PcbPlacement.HeatSink1PcbPlacementPosition.X,
            recipe.PcbPlacement.HeatSink1PcbPlacementPosition.Y),
        (recipe.PcbPlacement.HeatSink2PcbPlacementPosition.X,
            recipe.PcbPlacement.HeatSink2PcbPlacementPosition.Y)) != 0;

    public bool FasteningDefined =>
        carrier.IsDefined && (HasPins(fastening.ShootingHead) || HasPins(fastening.PickupHead));

    private static bool HasPins(BoltHeadSettings head) =>
        CarrierCoordinates.IsDefined(head.UpperLeftLocatingPin, head.LowerRightLocatingPin);

    private bool BothHeadsMapped =>
        fastening.ShootingHead.UpperLeftLocatingPin is { } first
        && fastening.ShootingHead.LowerRightLocatingPin is { } second
        && fastening.PickupHead.UpperLeftLocatingPin is { } pickup
        && CarrierCoordinates.IsDefined(
            carrier.UpperLeftLocatingPin,
            carrier.LowerRightLocatingPin)
        && MachinePlan.Side(
            (pickup.X, pickup.Y),
            (first.X, first.Y),
            (second.X, second.Y)) != 0;

    public bool InspectionDefined => carrier.IsDefined;

    private bool NgMapDefined
    {
        get
        {
            if (carrier.UpperLeftLocatingPin is not { } first
                || carrier.LowerRightLocatingPin is not { } second)
            {
                return false;
            }

            var pickup = transfer.CarrierPickupPosition;
            var shuttle = transfer.ShuttlePlacePosition;
            return MachinePlan.Side(
                       (pickup.X, pickup.Y),
                       (first.X, first.Y),
                       (second.X, second.Y))
                   * MachinePlan.Side(
                       (shuttle.X, shuttle.Y),
                       (first.X, first.Y),
                       (second.X, second.Y)) < 0;
        }
    }

    public (double X, double Y) Supply(MotionPosition current) =>
        FromThreePoints(
            current.X,
            current.Y,
            (recipe.PcbSupply.Pcb1PickPosition.X, supply.CarrierY),
            (recipe.PcbSupply.Pcb2PickPosition.X, supply.CarrierY),
            (supply.BufferHandoffPosition.X, supply.BufferHandoffPosition.Y),
            SupplyPcb1,
            SupplyPcb2,
            SupplyBuffer);

    public (double X, double Y) Placement(MotionPosition current) =>
        FromThreePoints(
            current.X,
            current.Y,
            (placement.BufferHandoffPosition.X, placement.BufferHandoffPosition.Y),
            (recipe.PcbPlacement.HeatSink1PcbPlacementPosition.X,
                recipe.PcbPlacement.HeatSink1PcbPlacementPosition.Y),
            (recipe.PcbPlacement.HeatSink2PcbPlacementPosition.X,
                recipe.PcbPlacement.HeatSink2PcbPlacementPosition.Y),
            PlacementBuffer,
            PlacementHeatSink1,
            PlacementHeatSink2);

    public (double X, double Y) Fastening(MotionPosition current) =>
        MapFastening(current.X, current.Y);

    public (double X, double Y) PickupFeeder()
    {
        var point = fastening.PickupPosition;
        var mapped = MapFastening(point.X, point.Y);
        return (mapped.X + PickupToolOffset.X + PickupFeederOffset.X,
            mapped.Y + PickupToolOffset.Y + PickupFeederOffset.Y);
    }

    public (double X, double Y) FasteningTarget(BoltPoint bolt)
    {
        var target = fastening.GetBoltPosition(bolt, carrier);
        var mapped = MapFastening(target.X, target.Y);
        var tool = bolt.Head == FasteningHead.Pickup
            ? PickupToolOffset
            : ShootingToolOffset;
        return (mapped.X + tool.X - BoltTargetOrigin.X,
            mapped.Y + tool.Y - BoltTargetOrigin.Y);
    }

    public (double X, double Y) Inspection(MotionPosition current) =>
        MapInspection(current.X, current.Y);

    public (double X, double Y) InspectionTarget(BoltPoint bolt)
    {
        var target = inspection.GetBoltPosition(bolt, carrier);
        var mapped = MapInspection(target.X, target.Y);
        return (mapped.X + MachinePlan.CameraCenter.X - MachinePlan.InspectionContentOrigin.X,
            mapped.Y + MachinePlan.CameraCenter.Y - MachinePlan.InspectionContentOrigin.Y);
    }

    private (double X, double Y) MapFastening(double x, double y)
    {
        if (!FasteningDefined) return default;
        if (!BothHeadsMapped)
        {
            var shooting = HasPins(fastening.ShootingHead);
            var head = shooting ? fastening.ShootingHead : fastening.PickupHead;
            var tool = shooting ? ShootingToolOffset : PickupToolOffset;
            return FromTwoPoints(x, y,
                (head.UpperLeftLocatingPin!.X, head.UpperLeftLocatingPin.Y),
                (head.LowerRightLocatingPin!.X, head.LowerRightLocatingPin.Y),
                MachinePlan.Offset(MachinePlan.FasteningUpperLeft, tool),
                MachinePlan.Offset(MachinePlan.FasteningLowerRight, tool));
        }

        var shootingUpperLeft = fastening.ShootingHead.UpperLeftLocatingPin!;
        var shootingLowerRight = fastening.ShootingHead.LowerRightLocatingPin!;
        var pickupUpperLeft = fastening.PickupHead.UpperLeftLocatingPin!;
        return FromThreePoints(
            x,
            y,
            (shootingUpperLeft.X, shootingUpperLeft.Y),
            (shootingLowerRight.X, shootingLowerRight.Y),
            (pickupUpperLeft.X, pickupUpperLeft.Y),
            ShootingUpperLeft,
            ShootingLowerRight,
            PickupUpperLeft);
    }

    private (double X, double Y) MapInspection(double x, double y)
    {
        if (!InspectionDefined) return default;
        var upperLeft = carrier.UpperLeftLocatingPin!;
        var lowerRight = carrier.LowerRightLocatingPin!;

        if (!NgMapDefined)
            return FromTwoPoints(x, y,
                (upperLeft.X, upperLeft.Y), (lowerRight.X, lowerRight.Y),
                MachinePlan.Offset(MachinePlan.InspectionUpperLeft, MachinePlan.CameraCenter),
                MachinePlan.Offset(MachinePlan.InspectionLowerRight, MachinePlan.CameraCenter));

        var pickup = transfer.CarrierPickupPosition;
        var shuttle = transfer.ShuttlePlacePosition;
        var first = (upperLeft.X, upperLeft.Y);
        var second = (lowerRight.X, lowerRight.Y);
        var pickupSide = MachinePlan.Side((x, y), first, second)
                         * MachinePlan.Side((pickup.X, pickup.Y), first, second) >= 0;
        return FromThreePoints(
            x,
            y,
            first,
            second,
            pickupSide ? (pickup.X, pickup.Y) : (shuttle.X, shuttle.Y),
            MachinePlan.Offset(MachinePlan.InspectionUpperLeft, MachinePlan.CameraCenter),
            MachinePlan.Offset(MachinePlan.InspectionLowerRight, MachinePlan.CameraCenter),
            MachinePlan.Offset(
                pickupSide ? MachinePlan.InspectionCarrierCenter : MachinePlan.NgShuttleCenter,
                MachinePlan.NgPickerCenter));
    }

    private static (double X, double Y) FromTwoPoints(
        double x, double y,
        (double X, double Y) first, (double X, double Y) second,
        (double X, double Y) targetFirst, (double X, double Y) targetSecond)
    {
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        var tx = targetSecond.X - targetFirst.X;
        var ty = targetSecond.Y - targetFirst.Y;
        // Diagonal pins define separate X/Y scales. An axis-aligned pair uses a similarity transform.
        if (dx != 0 && dy != 0)
            return (targetFirst.X + (x - first.X) * tx / dx,
                targetFirst.Y + (y - first.Y) * ty / dy);

        var lengthSquared = dx * dx + dy * dy;
        var a = (tx * dx + ty * dy) / lengthSquared;
        var b = (ty * dx - tx * dy) / lengthSquared;
        return (targetFirst.X + a * (x - first.X) - b * (y - first.Y),
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
        var denominator = ((source2.Y - source3.Y) * (source1.X - source3.X))
                          + ((source3.X - source2.X) * (source1.Y - source3.Y));
        if (denominator == 0)
        {
            return default;
        }

        var first = (((source2.Y - source3.Y) * (x - source3.X))
                     + ((source3.X - source2.X) * (y - source3.Y)))
                    / denominator;
        var second = (((source3.Y - source1.Y) * (x - source3.X))
                      + ((source1.X - source3.X) * (y - source3.Y)))
                     / denominator;
        var third = 1 - first - second;
        return ((first * target1.X) + (second * target2.X) + (third * target3.X),
            (first * target1.Y) + (second * target2.Y) + (third * target3.Y));
    }
}
