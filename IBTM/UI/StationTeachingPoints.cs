using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using static IBTM.UI.TeachingPoint;

namespace IBTM.UI;

public sealed class StationTeachingPoints(
    PcbPlacementHandlerSettings placementSettings,
    BoltFasteningSettings fasteningSettings,
    InspectionGantrySettings inspectionSettings,
    CarrierReferenceSettings carrierReference,
    NgCarrierTransferSettings ngCarrierTransferSettings)
{
    public bool CarrierReferenceReady =>
        CarrierCoordinates.IsDefined(
            carrierReference.UpperLeftLocatingPin,
            carrierReference.LowerRightLocatingPin);

    public Task SaveAsync(TeachingPoint point)
    {
        Setting settings = point.Target switch
        {
            TeachingTarget.PlacementBufferEntryZ => placementSettings,
            TeachingTarget.ShootingHeadUpperLeftLocatingPin
                or TeachingTarget.ShootingHeadLowerRightLocatingPin
                or TeachingTarget.PickupHeadUpperLeftLocatingPin
                or TeachingTarget.PickupHeadLowerRightLocatingPin
                or TeachingTarget.BoltPickup
                or TeachingTarget.BoltFasteningSafeZ => fasteningSettings,
            TeachingTarget.CarrierScanUpperLeft
                or TeachingTarget.CarrierScanLowerRight => inspectionSettings,
            TeachingTarget.CarrierUpperLeftLocatingPin
                or TeachingTarget.CarrierLowerRightLocatingPin => carrierReference,
            TeachingTarget.NgCarrierPickup
                or TeachingTarget.NgShuttlePlace => ngCarrierTransferSettings,
            _ => throw new ArgumentOutOfRangeException(nameof(point), point.Target, null),
        };
        return settings.SaveAsync();
    }

    public bool HasImagePosition(Recipe recipe, TeachingPoint point) =>
        point.Target switch
        {
            TeachingTarget.CarrierUpperLeftLocatingPin =>
                carrierReference.UpperLeftLocatingPin is not null,
            TeachingTarget.CarrierLowerRightLocatingPin =>
                carrierReference.LowerRightLocatingPin is not null,
            TeachingTarget.BoltReference =>
                CarrierReferenceReady
                && FindBolt(recipe, point) is { X: not null, Y: not null },
            _ => false,
        };

    public bool HasMotionPosition(Recipe recipe, TeachingPoint point) =>
        point.Target switch
        {
            TeachingTarget.BoltPointZ =>
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

    public List<TeachingPoint> Build(Recipe recipe)
    {
        List<TeachingPoint> points =
        [
            Create(
                TeachingTarget.PlacementBufferEntryZ,
                MotionGroup.PcbPlacementHandler,
                new AxisPosition { Z = placementSettings.BufferEntryZ },
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
                    ?? new AxisPosition(),
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.ShootingHeadLowerRightLocatingPin,
                MotionGroup.BoltFastening,
                fasteningSettings.ShootingHead.LowerRightLocatingPin
                    ?? new AxisPosition(),
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PickupHeadUpperLeftLocatingPin,
                MotionGroup.BoltFastening,
                fasteningSettings.PickupHead.UpperLeftLocatingPin
                    ?? new AxisPosition(),
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PickupHeadLowerRightLocatingPin,
                MotionGroup.BoltFastening,
                fasteningSettings.PickupHead.LowerRightLocatingPin
                    ?? new AxisPosition(),
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
                new AxisPosition { Z = fasteningSettings.SafeZ },
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
                carrierReference.UpperLeftLocatingPin ?? new AxisPosition(),
                TeachMode.Image,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.CarrierLowerRightLocatingPin,
                MotionGroup.InspectionGantry,
                carrierReference.LowerRightLocatingPin ?? new AxisPosition(),
                TeachMode.Image,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.NgCarrierPickup,
                MotionGroup.InspectionGantry,
                ngCarrierTransferSettings.CarrierPickupPosition,
                TeachMode.XYOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.NgShuttlePlace,
                MotionGroup.InspectionGantry,
                ngCarrierTransferSettings.ShuttlePlacePosition,
                TeachMode.XYOnly,
                TeachingStorage.Machine),
        ];

        points.AddRange(recipe.BoltFastening.BoltPoints.Select(bolt =>
            CreateBolt(
                TeachingTarget.BoltPointZ,
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
        var position = new AxisPosition
        {
            X = point.X,
            Y = point.Y,
            Z = point.Z!.Value,
        };

        switch (point.Target)
        {
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
            case TeachingTarget.BoltPointZ:
                FindBolt(recipe, point).Z = point.Z!.Value;
                break;
            case TeachingTarget.CarrierScanUpperLeft:
                inspectionSettings.CarrierScanUpperLeft = position;
                break;
            case TeachingTarget.CarrierScanLowerRight:
                inspectionSettings.CarrierScanLowerRight = position;
                break;
            case TeachingTarget.NgCarrierPickup:
                ngCarrierTransferSettings.CarrierPickupPosition = position;
                break;
            case TeachingTarget.NgShuttlePlace:
                ngCarrierTransferSettings.ShuttlePlacePosition = position;
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
        AxisPosition position)
    {
        point.Teach(position.X, position.Y, 0);
        switch (point.Target)
        {
            case TeachingTarget.CarrierUpperLeftLocatingPin:
                carrierReference.UpperLeftLocatingPin = position;
                carrierReference.LowerRightLocatingPin = null;
                UpdateInspectionBoltPositions(recipe, points);
                break;
            case TeachingTarget.CarrierLowerRightLocatingPin:
                carrierReference.LowerRightLocatingPin = position;
                UpdateInspectionBoltPositions(recipe, points);
                break;
            case TeachingTarget.BoltReference:
                var carrierPosition = CarrierCoordinates.FromMachine(
                    position,
                    carrierReference.UpperLeftLocatingPin!);
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
            var point = FindPoint(points, TeachingTarget.BoltPointZ, bolt.Number);
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

    private AxisPosition ToInspection(BoltPoint bolt)
    {
        if (!CarrierReferenceReady
            || bolt is not { X: not null, Y: not null })
        {
            return new AxisPosition();
        }

        return inspectionSettings.GetBoltPosition(
            bolt,
            carrierReference);
    }

    private AxisPosition ToFastening(BoltPoint bolt)
    {
        return HasFasteningPosition(bolt)
            ? fasteningSettings.GetBoltPosition(
                bolt,
                carrierReference)
            : new AxisPosition();
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

    private static TeachingPoint CreateBolt(
        TeachingTarget target,
        MotionGroup motionGroup,
        BoltPoint bolt,
        AxisPosition position,
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

}
