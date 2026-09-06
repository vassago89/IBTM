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
    private static readonly (double X, double Y) SupplyPcb1 = (60, 131);
    private static readonly (double X, double Y) SupplyPcb2 = (152, 131);
    private static readonly (double X, double Y) SupplyBuffer = (284, 191);
    private static readonly (double X, double Y) PlacementBuffer = (282, 209);
    private static readonly (double X, double Y) PlacementHeatSink1 = (220.25, 411);
    private static readonly (double X, double Y) PlacementHeatSink2 = (347.75, 411);
    private static readonly (double X, double Y) FasteningFallback = (18, 390);
    private static readonly (double X, double Y) ShootingUpperLeft = (-44.5, 355);
    private static readonly (double X, double Y) ShootingLowerRight = (187.5, 403);
    private static readonly (double X, double Y) PickupUpperLeft = (6.5, 355);
    private static readonly (double X, double Y) PickupToolOffset = (37.5, 101);
    private static readonly (double X, double Y) ShootingToolOffset = (88.5, 101);
    private static readonly (double X, double Y) BoltTargetOrigin = (44, 456);
    private static readonly (double X, double Y) PickupFeederOffset = (-50, -34);

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

    public bool InspectionDefined
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
        return (mapped.X + MachinePlan.CameraCenter.X - MachinePlan.InspectionUpperLeft.X,
            mapped.Y + MachinePlan.CameraCenter.Y - MachinePlan.InspectionUpperLeft.Y);
    }

    private (double X, double Y) MapFastening(double x, double y)
    {
        if (fastening.ShootingHead.UpperLeftLocatingPin is not { } shootingUpperLeft
            || fastening.ShootingHead.LowerRightLocatingPin is not { } shootingLowerRight
            || fastening.PickupHead.UpperLeftLocatingPin is not { } pickupUpperLeft)
        {
            return FasteningFallback;
        }

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
        if (carrier.UpperLeftLocatingPin is not { } upperLeft
            || carrier.LowerRightLocatingPin is not { } lowerRight)
        {
            return default;
        }

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
