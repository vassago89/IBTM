using System;
using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using IBTM.Sequence;
using IBTM.Stations.BoltFastening;

namespace IBTM.UI;

public sealed class TeachingPointMapper(MachineSettings settings)
{
    public List<TeachingPoint> BuildSupply(Recipe recipe) =>
        [
            Create(
                TeachingTarget.PcbSupplyCarrier1,
                EquipmentUnit.PcbSupply,
                ToAxisPos(recipe.PcbSupply.CarrierPick1),
                TeachMode.XZOnly),
            Create(
                TeachingTarget.PcbSupplyCarrier2,
                EquipmentUnit.PcbSupply,
                ToAxisPos(recipe.PcbSupply.CarrierPick2),
                TeachMode.XZOnly),
            Create(
                TeachingTarget.PcbSupplyRotation,
                EquipmentUnit.PcbSupply,
                new AxisPos
                {
                    X = settings.PcbSupply.RotationX,
                    Z = settings.PcbSupply.Motion.SafeZ,
                },
                TeachMode.XOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PcbSupplyHandoff,
                EquipmentUnit.PcbSupply,
                ToAxisPos(settings.PcbSupply.HandoffPosition),
                TeachMode.XZOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PcbPlacementHandoff,
                EquipmentUnit.PcbPlacement,
                settings.PcbPlacement.HandoffPickPosition,
                TeachMode.Full,
                TeachingStorage.Machine),
        ];

    public List<TeachingPoint> BuildStations(Recipe recipe)
    {
        List<TeachingPoint> points =
        [
            Create(
                TeachingTarget.Fiducial1,
                EquipmentUnit.PcbPlacement,
                recipe.PcbPlacement.Fiducial1Position,
                TeachMode.Full),
            Create(
                TeachingTarget.Fiducial2,
                EquipmentUnit.PcbPlacement,
                recipe.PcbPlacement.Fiducial2Position,
                TeachMode.Full),
            Create(
                TeachingTarget.Pcb1Place,
                EquipmentUnit.PcbPlacement,
                recipe.PcbPlacement.Pcb1PlacePosition,
                TeachMode.Full),
            Create(
                TeachingTarget.Pcb2Place,
                EquipmentUnit.PcbPlacement,
                recipe.PcbPlacement.Pcb2PlacePosition,
                TeachMode.Full),
            Create(
                TeachingTarget.BoltPcb1Reference,
                EquipmentUnit.BoltFastening,
                recipe.BoltFastening.Pcb1Reference,
                TeachMode.XYOnly),
            Create(
                TeachingTarget.BoltPcb2Reference,
                EquipmentUnit.BoltFastening,
                recipe.BoltFastening.Pcb2Reference,
                TeachMode.XYOnly),
            Create(
                TeachingTarget.Pcb1Inspection,
                EquipmentUnit.Inspection,
                recipe.Inspection.Pcb1InspectionPosition,
                TeachMode.Full),
            Create(
                TeachingTarget.Pcb2Inspection,
                EquipmentUnit.Inspection,
                recipe.Inspection.Pcb2InspectionPosition,
                TeachMode.Full),
            Create(
                TeachingTarget.NgCarrierPickup,
                EquipmentUnit.Inspection,
                recipe.Inspection.NgCarrierPickupPosition,
                TeachMode.Full),
            Create(
                TeachingTarget.NgStack,
                EquipmentUnit.Inspection,
                recipe.Inspection.NgStackPosition,
                TeachMode.Full),
        ];

        points.AddRange(recipe.BoltFastening.BoltPoints.Select(bolt =>
            CreateBolt(
                TeachingTarget.BoltZ,
                EquipmentUnit.BoltFastening,
                bolt,
                ToBoltFastening(recipe, bolt),
                TeachMode.ZOnly,
                bolt.Z != 0)));
        points.AddRange(recipe.BoltFastening.BoltPoints.Select(bolt =>
            CreateBolt(
                TeachingTarget.BoltReference,
                EquipmentUnit.Inspection,
                bolt,
                ToInspection(recipe, bolt),
                TeachMode.XYOnly,
                bolt.X != 0 || bolt.Y != 0)));

        return points;
    }

    public void Apply(
        Recipe recipe,
        IReadOnlyCollection<TeachingPoint> points,
        TeachingPoint point)
    {
        var position = new AxisPos { X = point.X, Y = point.Y, Z = point.Z };

        switch (point.Target)
        {
            case TeachingTarget.PcbSupplyCarrier1:
                recipe.PcbSupply.CarrierPick1 = ToXzPos(position);
                break;
            case TeachingTarget.PcbSupplyCarrier2:
                recipe.PcbSupply.CarrierPick2 = ToXzPos(position);
                break;
            case TeachingTarget.PcbSupplyRotation:
                settings.PcbSupply.RotationX = position.X;
                break;
            case TeachingTarget.PcbSupplyHandoff:
                settings.PcbSupply.HandoffPosition = ToXzPos(position);
                break;
            case TeachingTarget.PcbPlacementHandoff:
                settings.PcbPlacement.HandoffPickPosition = position;
                break;
            case TeachingTarget.Fiducial1:
                recipe.PcbPlacement.Fiducial1Position = position;
                break;
            case TeachingTarget.Fiducial2:
                recipe.PcbPlacement.Fiducial2Position = position;
                break;
            case TeachingTarget.Pcb1Place:
                recipe.PcbPlacement.Pcb1PlacePosition = position;
                break;
            case TeachingTarget.Pcb2Place:
                recipe.PcbPlacement.Pcb2PlacePosition = position;
                break;
            case TeachingTarget.BoltPcb1Reference:
                recipe.BoltFastening.Pcb1Reference = position;
                UpdateBoltPositions(recipe, points);
                break;
            case TeachingTarget.BoltPcb2Reference:
                recipe.BoltFastening.Pcb2Reference = position;
                break;
            case TeachingTarget.BoltZ:
                FindBolt(recipe, point).Z = point.Z;
                break;
            case TeachingTarget.Pcb1Inspection:
                recipe.Inspection.Pcb1InspectionPosition = position;
                UpdateInspectionBoltPositions(recipe, points);
                break;
            case TeachingTarget.Pcb2Inspection:
                recipe.Inspection.Pcb2InspectionPosition = position;
                break;
            case TeachingTarget.NgCarrierPickup:
                recipe.Inspection.NgCarrierPickupPosition = position;
                break;
            case TeachingTarget.NgStack:
                recipe.Inspection.NgStackPosition = position;
                break;
            case TeachingTarget.BoltReference:
                ApplyBoltReference(recipe, points, point, position);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(point.Target));
        }
    }

    private static AxisPos ToAxisPos(XzPos position) =>
        new() { X = position.X, Z = position.Z };

    private static XzPos ToXzPos(AxisPos position) =>
        new() { X = position.X, Z = position.Z };

    private AxisPos ToBoltFastening(Recipe recipe, BoltPoint bolt)
    {
        var reference = recipe.BoltFastening.Pcb1Reference;
        var head = settings.BoltFastening.GetHead(bolt.BoltType);
        return new AxisPos
        {
            X = reference.X + bolt.X - head.OffsetX,
            Y = reference.Y + bolt.Y - head.OffsetY,
            Z = bolt.Z,
        };
    }

    private static AxisPos ToInspection(Recipe recipe, BoltPoint bolt)
    {
        var reference = recipe.Inspection.Pcb1InspectionPosition;
        return new AxisPos
        {
            X = reference.X + bolt.X,
            Y = reference.Y + bolt.Y,
        };
    }

    private void ApplyBoltReference(
        Recipe recipe,
        IReadOnlyCollection<TeachingPoint> points,
        TeachingPoint point,
        AxisPos position)
    {
        var bolt = FindBolt(recipe, point);
        var reference = recipe.Inspection.Pcb1InspectionPosition;
        bolt.X = position.X - reference.X;
        bolt.Y = position.Y - reference.Y;

        var boltPosition = ToBoltFastening(recipe, bolt);
        var boltZ = FindPoint(points, TeachingTarget.BoltZ, bolt.Number);
        boltZ.X = boltPosition.X;
        boltZ.Y = boltPosition.Y;
    }

    private void UpdateBoltPositions(
        Recipe recipe,
        IEnumerable<TeachingPoint> points)
    {
        foreach (var bolt in recipe.BoltFastening.BoltPoints)
        {
            var position = ToBoltFastening(recipe, bolt);
            var point = FindPoint(points, TeachingTarget.BoltZ, bolt.Number);
            point.X = position.X;
            point.Y = position.Y;
        }
    }

    private static void UpdateInspectionBoltPositions(
        Recipe recipe,
        IEnumerable<TeachingPoint> points)
    {
        foreach (var bolt in recipe.BoltFastening.BoltPoints)
        {
            var position = ToInspection(recipe, bolt);
            var point = FindPoint(
                points,
                TeachingTarget.BoltReference,
                bolt.Number);
            point.X = position.X;
            point.Y = position.Y;
        }
    }

    private static TeachingPoint Create(
        TeachingTarget target,
        EquipmentUnit unit,
        AxisPos position,
        TeachMode mode,
        TeachingStorage storage = TeachingStorage.Recipe) => new()
        {
            Target = target,
            Unit = unit,
            TeachMode = mode,
            Storage = storage,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            IsTaught = IsSet(position),
        };

    private static TeachingPoint CreateBolt(
        TeachingTarget target,
        EquipmentUnit unit,
        BoltPoint bolt,
        AxisPos position,
        TeachMode mode,
        bool isTaught) => new()
        {
            Target = target,
            Unit = unit,
            TeachMode = mode,
            BoltNumber = bolt.Number,
            BoltType = bolt.BoltType,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            IsTaught = isTaught,
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

    private static bool IsSet(AxisPos position) =>
        position.X != 0 || position.Y != 0 || position.Z != 0;
}
