using System;
using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using IBTM.Stations.BoltFastening;

namespace IBTM.UI;

public sealed class TeachingPointMapper(MachineSettings settings)
{
    public List<TeachingPoint> BuildSupply(Recipe recipe) =>
        [
            Create(
                TeachingTarget.SupplyPcb1Pick,
                MotionGroup.PcbSupply,
                ToAxisPos(recipe.PcbSupply.Pcb1PickPosition),
                TeachMode.XZOnly),
            Create(
                TeachingTarget.SupplyPcb2Pick,
                MotionGroup.PcbSupply,
                ToAxisPos(recipe.PcbSupply.Pcb2PickPosition),
                TeachMode.XZOnly),
            Create(
                TeachingTarget.SupplyBuffer,
                MotionGroup.PcbSupply,
                ToAxisPos(settings.PcbSupply.BufferPosition),
                TeachMode.XZOnly,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.PlacementBuffer,
                MotionGroup.PcbPlacement,
                settings.PcbPlacement.BufferPosition,
                TeachMode.Full,
                TeachingStorage.Machine),
        ];

    public List<TeachingPoint> BuildStations(Recipe recipe)
    {
        List<TeachingPoint> points =
        [
            Create(
                TeachingTarget.Fiducial1Capture,
                MotionGroup.PcbPlacement,
                recipe.PcbPlacement.Fiducial1Position,
                TeachMode.Full),
            Create(
                TeachingTarget.Fiducial2Capture,
                MotionGroup.PcbPlacement,
                recipe.PcbPlacement.Fiducial2Position,
                TeachMode.Full),
            Create(
                TeachingTarget.Pcb1Placement,
                MotionGroup.PcbPlacement,
                recipe.PcbPlacement.Pcb1PlacementPosition,
                TeachMode.Full),
            Create(
                TeachingTarget.Pcb2Placement,
                MotionGroup.PcbPlacement,
                recipe.PcbPlacement.Pcb2PlacementPosition,
                TeachMode.Full),
            Create(
                TeachingTarget.BoltPcb1Reference,
                MotionGroup.BoltFastening,
                recipe.BoltFastening.Pcb1Reference,
                TeachMode.XYOnly),
            Create(
                TeachingTarget.BoltPcb2Reference,
                MotionGroup.BoltFastening,
                recipe.BoltFastening.Pcb2Reference,
                TeachMode.XYOnly),
            Create(
                TeachingTarget.LoctiteBoltPickup,
                MotionGroup.BoltFastening,
                settings.BoltFastening.LoctitePickupPosition,
                TeachMode.Full,
                TeachingStorage.Machine),
            Create(
                TeachingTarget.Pcb1InspectionCapture,
                MotionGroup.Inspection,
                recipe.Inspection.Pcb1InspectionPosition,
                TeachMode.Full),
            Create(
                TeachingTarget.Pcb2InspectionCapture,
                MotionGroup.Inspection,
                recipe.Inspection.Pcb2InspectionPosition,
                TeachMode.Full),
            Create(
                TeachingTarget.NgCarrierJigPickup,
                MotionGroup.Inspection,
                recipe.Inspection.NgCarrierJigPickupPosition,
                TeachMode.Full),
            Create(
                TeachingTarget.NgShuttle,
                MotionGroup.Inspection,
                recipe.Inspection.NgShuttlePosition,
                TeachMode.Full),
        ];

        points.AddRange(recipe.BoltFastening.BoltPoints.Select(bolt =>
            CreateBolt(
                TeachingTarget.BoltWorkZ,
                MotionGroup.BoltFastening,
                bolt,
                ToBoltFastening(recipe, bolt),
                TeachMode.ZOnly,
                bolt.Z != 0)));
        points.AddRange(recipe.BoltFastening.BoltPoints.Select(bolt =>
            CreateBolt(
                TeachingTarget.BoltReference,
                MotionGroup.Inspection,
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
            case TeachingTarget.SupplyPcb1Pick:
                recipe.PcbSupply.Pcb1PickPosition = ToXzPos(position);
                break;
            case TeachingTarget.SupplyPcb2Pick:
                recipe.PcbSupply.Pcb2PickPosition = ToXzPos(position);
                break;
            case TeachingTarget.SupplyBuffer:
                settings.PcbSupply.BufferPosition = ToXzPos(position);
                break;
            case TeachingTarget.PlacementBuffer:
                settings.PcbPlacement.BufferPosition = position;
                break;
            case TeachingTarget.Fiducial1Capture:
                recipe.PcbPlacement.Fiducial1Position = position;
                break;
            case TeachingTarget.Fiducial2Capture:
                recipe.PcbPlacement.Fiducial2Position = position;
                break;
            case TeachingTarget.Pcb1Placement:
                recipe.PcbPlacement.Pcb1PlacementPosition = position;
                break;
            case TeachingTarget.Pcb2Placement:
                recipe.PcbPlacement.Pcb2PlacementPosition = position;
                break;
            case TeachingTarget.BoltPcb1Reference:
                recipe.BoltFastening.Pcb1Reference = position;
                UpdateBoltPositions(recipe, points);
                break;
            case TeachingTarget.BoltPcb2Reference:
                recipe.BoltFastening.Pcb2Reference = position;
                break;
            case TeachingTarget.LoctiteBoltPickup:
                settings.BoltFastening.LoctitePickupPosition = position;
                break;
            case TeachingTarget.BoltWorkZ:
                FindBolt(recipe, point).Z = point.Z;
                break;
            case TeachingTarget.Pcb1InspectionCapture:
                recipe.Inspection.Pcb1InspectionPosition = position;
                UpdateInspectionBoltPositions(recipe, points);
                break;
            case TeachingTarget.Pcb2InspectionCapture:
                recipe.Inspection.Pcb2InspectionPosition = position;
                break;
            case TeachingTarget.NgCarrierJigPickup:
                recipe.Inspection.NgCarrierJigPickupPosition = position;
                break;
            case TeachingTarget.NgShuttle:
                recipe.Inspection.NgShuttlePosition = position;
                break;
            case TeachingTarget.BoltReference:
                ApplyBoltReference(recipe, points, point, position);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(point),
                    point.Target,
                    null);
        }
    }

    private static AxisPos ToAxisPos(XzPos position) =>
        new() { X = position.X, Z = position.Z };

    private static XzPos ToXzPos(AxisPos position) =>
        new() { X = position.X, Z = position.Z };

    private AxisPos ToBoltFastening(Recipe recipe, BoltPoint bolt)
    {
        var reference = recipe.BoltFastening.Pcb1Reference;
        var head = settings.BoltFastening.GetHead(bolt.Head);
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
        var boltZ = FindPoint(points, TeachingTarget.BoltWorkZ, bolt.Number);
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
            var point = FindPoint(points, TeachingTarget.BoltWorkZ, bolt.Number);
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
            IsTaught = IsSet(position),
        };

    private static TeachingPoint CreateBolt(
        TeachingTarget target,
        MotionGroup motionGroup,
        BoltPoint bolt,
        AxisPos position,
        TeachMode mode,
        bool isTaught) => new()
        {
            Target = target,
            MotionGroup = motionGroup,
            TeachMode = mode,
            BoltNumber = bolt.Number,
            Head = bolt.Head,
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
