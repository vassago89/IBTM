using System;
using System.Collections.Generic;
using System.Linq;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM.UI;

public sealed class TeachingPointMapper(
    PcbBufferSettings bufferSettings,
    PcbSupplySettings supplySettings,
    PcbPlacementHandlerSettings placementSettings,
    BoltFasteningSettings fasteningSettings,
    InspectionGantrySettings inspectionSettings,
    CarrierReferenceSettings carrierReference,
    NgConveyorSettings ngConveyorSettings)
{
    public bool CarrierReferenceReady =>
        CarrierCoordinates.IsDefined(
            carrierReference.UpperLeftPin,
            carrierReference.LowerRightPin);

    public bool HasImagePosition(Recipe recipe, TeachingPoint point) =>
        point.Target switch
        {
            TeachingTarget.CarrierUpperLeftLocatingPin =>
                carrierReference.UpperLeftPin is not null,
            TeachingTarget.CarrierLowerRightLocatingPin =>
                carrierReference.LowerRightPin is not null,
            TeachingTarget.BoltReference =>
                CarrierReferenceReady
                && FindBolt(recipe, point) is { X: not null, Y: not null },
            _ => false,
        };

    public bool HasMotionPosition(Recipe recipe, TeachingPoint point) =>
        point.Target switch
        {
            TeachingTarget.BoltWorkZ =>
                HasFasteningPosition(FindBolt(recipe, point)),
            TeachingTarget.ShootingHeadUpperLeftLocatingPin =>
                fasteningSettings.ShootingHead.UpperLeftLocatingPin is not null,
            TeachingTarget.ShootingHeadLowerRightLocatingPin =>
                fasteningSettings.ShootingHead.LowerRightLocatingPin is not null,
            TeachingTarget.PickupHeadUpperLeftLocatingPin =>
                fasteningSettings.PickupHead.UpperLeftLocatingPin is not null,
            TeachingTarget.PickupHeadLowerRightLocatingPin =>
                fasteningSettings.PickupHead.LowerRightLocatingPin is not null,
            _ => true,
        };

    public List<TeachingPoint> BuildSupply(Recipe recipe) =>
        [
            Create(
                TeachingTarget.SupplyRotationZ,
                MotionGroup.PcbSupply,
                Z(supplySettings.RotationZ),
                TeachMode.ZOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.SupplyCarrierY,
                MotionGroup.PcbSupply,
                new AxisPos { Y = supplySettings.CarrierY },
                TeachMode.YOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.SupplyPcb1Pick,
                MotionGroup.PcbSupply,
                new AxisPos
                {
                    X = recipe.PcbSupply.Pcb1PickPosition.X,
                    Y = supplySettings.CarrierY,
                    Z = recipe.PcbSupply.Pcb1PickPosition.Z,
                },
                TeachMode.XZOnly),
            Create(
                TeachingTarget.SupplyPcb2Pick,
                MotionGroup.PcbSupply,
                new AxisPos
                {
                    X = recipe.PcbSupply.Pcb2PickPosition.X,
                    Y = supplySettings.CarrierY,
                    Z = recipe.PcbSupply.Pcb2PickPosition.Z,
                },
                TeachMode.XZOnly),
            Create(
                TeachingTarget.SupplyBufferHandoff,
                MotionGroup.PcbSupply,
                supplySettings.BufferHandoffPosition,
                TeachMode.Full,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.SupplyBufferClearZ,
                MotionGroup.PcbSupply,
                new AxisPos { Z = supplySettings.BufferClearZ },
                TeachMode.ZOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PlacementBufferHandoff,
                MotionGroup.PcbPlacementHandler,
                placementSettings.BufferHandoffPosition,
                TeachMode.Full,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.SupplyBufferBoundary1,
                MotionGroup.PcbSupply,
                new AxisPos { X = bufferSettings.SupplyBoundary1 },
                TeachMode.XOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.SupplyBufferBoundary2,
                MotionGroup.PcbSupply,
                new AxisPos { X = bufferSettings.SupplyBoundary2 },
                TeachMode.XOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PlacementBufferBoundary1,
                MotionGroup.PcbPlacementHandler,
                bufferSettings.PlacementBoundary1,
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PlacementBufferBoundary2,
                MotionGroup.PcbPlacementHandler,
                bufferSettings.PlacementBoundary2,
                TeachMode.XYOnly,
                TeachingStorage.Machine),
        ];

    public List<TeachingPoint> BuildStations(Recipe recipe)
    {
        List<TeachingPoint> points =
        [
            Create(
                TeachingTarget.PlacementBufferEntryZ,
                MotionGroup.PcbPlacementHandler,
                Z(placementSettings.BufferEntryZ),
                TeachMode.ZOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.HeatSink1PcbPlacement,
                MotionGroup.PcbPlacementHandler,
                recipe.PcbPlacement.HeatSink1PcbPlacementPosition,
                TeachMode.Full),
            Create(
                TeachingTarget.HeatSink2PcbPlacement,
                MotionGroup.PcbPlacementHandler,
                recipe.PcbPlacement.HeatSink2PcbPlacementPosition,
                TeachMode.Full),
            Create(
                TeachingTarget.ShootingHeadUpperLeftLocatingPin,
                MotionGroup.BoltFastening,
                fasteningSettings.ShootingHead.UpperLeftLocatingPin
                    ?? new AxisPos(),
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.ShootingHeadLowerRightLocatingPin,
                MotionGroup.BoltFastening,
                fasteningSettings.ShootingHead.LowerRightLocatingPin
                    ?? new AxisPos(),
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PickupHeadUpperLeftLocatingPin,
                MotionGroup.BoltFastening,
                fasteningSettings.PickupHead.UpperLeftLocatingPin
                    ?? new AxisPos(),
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PickupHeadLowerRightLocatingPin,
                MotionGroup.BoltFastening,
                fasteningSettings.PickupHead.LowerRightLocatingPin
                    ?? new AxisPos(),
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.BoltPickup,
                MotionGroup.BoltFastening,
                fasteningSettings.PickupPosition,
                TeachMode.Full,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.BoltFasteningSafeZ,
                MotionGroup.BoltFastening,
                Z(fasteningSettings.SafeZ),
                TeachMode.ZOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.CarrierScanUpperLeft,
                MotionGroup.InspectionGantry,
                inspectionSettings.CarrierScanUpperLeft,
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.CarrierScanLowerRight,
                MotionGroup.InspectionGantry,
                inspectionSettings.CarrierScanLowerRight,
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.CarrierUpperLeftLocatingPin,
                MotionGroup.InspectionGantry,
                carrierReference.UpperLeftPin ?? new AxisPos(),
                TeachMode.Image,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.CarrierLowerRightLocatingPin,
                MotionGroup.InspectionGantry,
                carrierReference.LowerRightPin ?? new AxisPos(),
                TeachMode.Image,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.NgCarrierPickup,
                MotionGroup.InspectionGantry,
                ngConveyorSettings.CarrierPickupPosition,
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.NgShuttlePlace,
                MotionGroup.InspectionGantry,
                ngConveyorSettings.ShuttlePlacePosition,
                TeachMode.XYOnly,
                TeachingStorage.Machine),
        ];

        points.AddRange(recipe.BoltFastening.BoltPoints.Select(bolt =>
            CreateBolt(
                TeachingTarget.BoltWorkZ,
                MotionGroup.BoltFastening,
                bolt,
                ToFastening(bolt),
                TeachMode.ZOnly)));
        points.AddRange(recipe.BoltFastening.BoltPoints.Select(bolt =>
            CreateBolt(
                TeachingTarget.BoltReference,
                MotionGroup.InspectionGantry,
                bolt,
                ToInspection(bolt),
                TeachMode.Image)));

        return points;
    }

    public void Apply(
        Recipe recipe,
        IReadOnlyCollection<TeachingPoint> points,
        TeachingPoint point)
    {
        var position = new AxisPos
        {
            X = point.X,
            Y = point.Y,
            Z = point.Z!.Value,
        };

        switch (point.Target)
        {
            case TeachingTarget.SupplyRotationZ:
                supplySettings.RotationZ = point.Z!.Value;
                break;
            case TeachingTarget.SupplyCarrierY:
                supplySettings.CarrierY = point.Y;
                foreach (var pickPoint in points.Where(candidate =>
                             candidate.Target is TeachingTarget.SupplyPcb1Pick
                                 or TeachingTarget.SupplyPcb2Pick))
                {
                    pickPoint.Y = point.Y;
                }
                break;
            case TeachingTarget.SupplyPcb1Pick:
                recipe.PcbSupply.Pcb1PickPosition.X = point.X;
                recipe.PcbSupply.Pcb1PickPosition.Z = point.Z!.Value;
                break;
            case TeachingTarget.SupplyPcb2Pick:
                recipe.PcbSupply.Pcb2PickPosition.X = point.X;
                recipe.PcbSupply.Pcb2PickPosition.Z = point.Z!.Value;
                break;
            case TeachingTarget.SupplyBufferHandoff:
                supplySettings.BufferHandoffPosition.X = point.X;
                supplySettings.BufferHandoffPosition.Y = point.Y;
                supplySettings.BufferHandoffPosition.Z = point.Z!.Value;
                break;
            case TeachingTarget.SupplyBufferClearZ:
                supplySettings.BufferClearZ = point.Z!.Value;
                break;
            case TeachingTarget.PlacementBufferHandoff:
                placementSettings.BufferHandoffPosition.X = point.X;
                placementSettings.BufferHandoffPosition.Y = point.Y;
                placementSettings.BufferHandoffPosition.Z = point.Z!.Value;
                break;
            case TeachingTarget.SupplyBufferBoundary1:
                bufferSettings.SupplyBoundary1 = point.X;
                break;
            case TeachingTarget.SupplyBufferBoundary2:
                bufferSettings.SupplyBoundary2 = point.X;
                break;
            case TeachingTarget.PlacementBufferBoundary1:
                bufferSettings.PlacementBoundary1 = position;
                break;
            case TeachingTarget.PlacementBufferBoundary2:
                bufferSettings.PlacementBoundary2 = position;
                break;
            case TeachingTarget.PlacementBufferEntryZ:
                placementSettings.BufferEntryZ = point.Z!.Value;
                break;
            case TeachingTarget.HeatSink1PcbPlacement:
                recipe.PcbPlacement.HeatSink1PcbPlacementPosition = position;
                break;
            case TeachingTarget.HeatSink2PcbPlacement:
                recipe.PcbPlacement.HeatSink2PcbPlacementPosition = position;
                break;
            case TeachingTarget.ShootingHeadUpperLeftLocatingPin:
                fasteningSettings.ShootingHead.UpperLeftLocatingPin = position;
                fasteningSettings.ShootingHead.LowerRightLocatingPin = null;
                UpdateBoltPositions(recipe, points);
                break;
            case TeachingTarget.ShootingHeadLowerRightLocatingPin:
                fasteningSettings.ShootingHead.LowerRightLocatingPin = position;
                UpdateBoltPositions(recipe, points);
                break;
            case TeachingTarget.PickupHeadUpperLeftLocatingPin:
                fasteningSettings.PickupHead.UpperLeftLocatingPin = position;
                fasteningSettings.PickupHead.LowerRightLocatingPin = null;
                UpdateBoltPositions(recipe, points);
                break;
            case TeachingTarget.PickupHeadLowerRightLocatingPin:
                fasteningSettings.PickupHead.LowerRightLocatingPin = position;
                UpdateBoltPositions(recipe, points);
                break;
            case TeachingTarget.BoltPickup:
                fasteningSettings.PickupPosition = position;
                break;
            case TeachingTarget.BoltFasteningSafeZ:
                fasteningSettings.SafeZ = point.Z!.Value;
                break;
            case TeachingTarget.BoltWorkZ:
                FindBolt(recipe, point).Z = point.Z!.Value;
                break;
            case TeachingTarget.CarrierScanUpperLeft:
                inspectionSettings.CarrierScanUpperLeft = position;
                break;
            case TeachingTarget.CarrierScanLowerRight:
                inspectionSettings.CarrierScanLowerRight = position;
                break;
            case TeachingTarget.NgCarrierPickup:
                ngConveyorSettings.CarrierPickupPosition = position;
                break;
            case TeachingTarget.NgShuttlePlace:
                ngConveyorSettings.ShuttlePlacePosition = position;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(point),
                    point.Target,
                    null);
        }
    }

    public void ApplyImage(
        Recipe recipe,
        IReadOnlyCollection<TeachingPoint> points,
        TeachingPoint point,
        AxisPos position)
    {
        point.Teach(position.X, position.Y, 0);
        switch (point.Target)
        {
            case TeachingTarget.CarrierUpperLeftLocatingPin:
                carrierReference.UpperLeftPin = position;
                carrierReference.LowerRightPin = null;
                UpdateInspectionBoltPositions(recipe, points);
                break;
            case TeachingTarget.CarrierLowerRightLocatingPin:
                carrierReference.LowerRightPin = position;
                UpdateInspectionBoltPositions(recipe, points);
                break;
            case TeachingTarget.BoltReference:
                var carrierPosition = CarrierCoordinates.FromMachine(
                    position,
                    carrierReference.UpperLeftPin!);
                var bolt = FindBolt(recipe, point);
                bolt.X = carrierPosition.X;
                bolt.Y = carrierPosition.Y;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(point),
                    point.Target,
                    null);
        }
    }

    private void UpdateBoltPositions(
        Recipe recipe,
        IEnumerable<TeachingPoint> points)
    {
        foreach (var bolt in recipe.BoltFastening.BoltPoints)
        {
            var position = ToFastening(bolt);
            var point = FindPoint(points, TeachingTarget.BoltWorkZ, bolt.Number);
            point.X = position.X;
            point.Y = position.Y;
        }
    }

    private void UpdateInspectionBoltPositions(
        Recipe recipe,
        IEnumerable<TeachingPoint> points)
    {
        foreach (var bolt in recipe.BoltFastening.BoltPoints)
        {
            var position = ToInspection(bolt);
            var point = FindPoint(
                points,
                TeachingTarget.BoltReference,
                bolt.Number);
            point.X = position.X;
            point.Y = position.Y;
        }
    }

    private AxisPos ToInspection(BoltPoint bolt)
    {
        if (!CarrierReferenceReady
            || bolt is not { X: not null, Y: not null })
        {
            return new AxisPos();
        }

        return inspectionSettings.GetBoltPosition(
            bolt,
            carrierReference);
    }

    private AxisPos ToFastening(BoltPoint bolt)
    {
        return HasFasteningPosition(bolt)
            ? fasteningSettings.GetBoltPosition(
                bolt,
                carrierReference)
            : new AxisPos();
    }

    private bool HasFasteningPosition(BoltPoint bolt)
    {
        var head = fasteningSettings.GetHead(bolt.Head);
        return CarrierReferenceReady
            && CarrierCoordinates.IsDefined(
                head.UpperLeftLocatingPin,
                head.LowerRightLocatingPin)
            && bolt is { X: not null, Y: not null, Z: not null };
    }

    private static TeachingPoint Create(
        TeachingTarget target,
        MotionGroup motionGroup,
        AxisPos position,
        TeachMode mode,
        TeachingStorage storage = TeachingStorage.Recipe) => new()
        {
            Target = target,
            MotionGroup = motionGroup,
            TeachMode = mode,
            Storage = storage,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        };

    private static TeachingPoint CreateBolt(
        TeachingTarget target,
        MotionGroup motionGroup,
        BoltPoint bolt,
        AxisPos position,
        TeachMode mode) => new()
        {
            Target = target,
            MotionGroup = motionGroup,
            TeachMode = mode,
            BoltNumber = bolt.Number,
            HeatSink = bolt.HeatSink,
            Head = bolt.Head,
            X = position.X,
            Y = position.Y,
            Z = bolt.Z,
        };

    private static BoltPoint FindBolt(Recipe recipe, TeachingPoint point) =>
        recipe.BoltFastening.BoltPoints.Single(
            bolt => bolt.Number == point.BoltNumber);

    private static TeachingPoint FindPoint(
        IEnumerable<TeachingPoint> points,
        TeachingTarget target,
        int boltNumber) =>
        points.Single(point =>
            point.Target == target
            && point.BoltNumber == boltNumber);

    private static AxisPos Z(double value) => new()
    {
        Z = value,
    };
}
