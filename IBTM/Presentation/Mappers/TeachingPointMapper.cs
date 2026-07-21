using System.Collections.Generic;
using System.Linq;
using IBTM.Configuration;
using IBTM.Core.Geometry;
using IBTM.Orchestration;
using IBTM.Presentation.Models;

namespace IBTM.Presentation.Mappers;

public sealed class TeachingPointMapper(MachineConfig config)
{
    public List<TeachingPoint> Build(Recipe recipe)
    {
        var points = new List<TeachingPoint>
        {
            Create("PcbPick1", TeachingPointKind.PcbPick, 1, recipe.PcbPlacement.PcbPick1, TeachMode.Full),
            Create("PcbPick2", TeachingPointKind.PcbPick, 1, recipe.PcbPlacement.PcbPick2, TeachMode.Full),
            Create("PcbPlace1", TeachingPointKind.PcbPlaceZ, 1, recipe.PcbPlacement.PcbPlace1, TeachMode.ZOnly),
            Create("PcbPlace2", TeachingPointKind.PcbPlaceZ, 1, recipe.PcbPlacement.PcbPlace2, TeachMode.ZOnly),
            Create("Fiducial", TeachingPointKind.Fiducial, 2, recipe.BoltFastening.FiducialPosition, TeachMode.Full),
        };

        points.AddRange(recipe.BoltFastening.BoltPoints.Select(bolt => new TeachingPoint
        {
            Name = bolt.Name,
            Kind = TeachingPointKind.BoltZ,
            Station = 2,
            TeachMode = TeachMode.ZOnly,
            X = bolt.X,
            Y = bolt.Y,
            Z = bolt.Z,
            IsTaught = bolt.Z != 0,
        }));

        points.Add(Create(
            "InspectPos",
            TeachingPointKind.Inspection,
            3,
            recipe.Inspection.InspectPosition,
            TeachMode.Full));
        points.Add(Create(
            "NgCarrierPickup",
            TeachingPointKind.NgCarrierPickup,
            3,
            recipe.Inspection.NgCarrierPickupPosition,
            TeachMode.Full));
        points.Add(Create(
            "NgStack",
            TeachingPointKind.NgStack,
            3,
            recipe.Inspection.NgStackPosition,
            TeachMode.Full));
        points.Add(Create(
            "PcbPlace1",
            TeachingPointKind.PcbPlaceReference,
            3,
            config.Calibration.FromPcbPlacementToInspection(recipe.PcbPlacement.PcbPlace1),
            TeachMode.XYOnly));
        points.Add(Create(
            "PcbPlace2",
            TeachingPointKind.PcbPlaceReference,
            3,
            config.Calibration.FromPcbPlacementToInspection(recipe.PcbPlacement.PcbPlace2),
            TeachMode.XYOnly));

        points.AddRange(recipe.BoltFastening.BoltPoints.Select(bolt =>
        {
            var position = config.Calibration.FromBoltFasteningToInspection(
                new AxisPos { X = bolt.X, Y = bolt.Y });
            return new TeachingPoint
            {
                Name = bolt.Name,
                Kind = TeachingPointKind.BoltReference,
                Station = 3,
                TeachMode = TeachMode.XYOnly,
                X = position.X,
                Y = position.Y,
                IsTaught = bolt.X != 0 || bolt.Y != 0,
            };
        }));

        return points;
    }

    public void Apply(Recipe recipe, IReadOnlyCollection<TeachingPoint> points, TeachingPoint point)
    {
        var position = new AxisPos { X = point.X, Y = point.Y, Z = point.Z };

        switch (point.Kind)
        {
            case TeachingPointKind.PcbPick:
                if (point.Name == "PcbPick1")
                {
                    recipe.PcbPlacement.PcbPick1 = position;
                }
                else
                {
                    recipe.PcbPlacement.PcbPick2 = position;
                }
                break;

            case TeachingPointKind.PcbPlaceZ:
                var place = point.Name == "PcbPlace1"
                    ? recipe.PcbPlacement.PcbPlace1
                    : recipe.PcbPlacement.PcbPlace2;
                place.Z = point.Z;
                break;

            case TeachingPointKind.Fiducial:
                recipe.BoltFastening.FiducialPosition = position;
                break;

            case TeachingPointKind.BoltZ:
                recipe.BoltFastening.BoltPoints.Single(bolt => bolt.Name == point.Name).Z = point.Z;
                break;

            case TeachingPointKind.Inspection:
                recipe.Inspection.InspectPosition = position;
                break;

            case TeachingPointKind.NgCarrierPickup:
                recipe.Inspection.NgCarrierPickupPosition = position;
                break;

            case TeachingPointKind.NgStack:
                recipe.Inspection.NgStackPosition = position;
                break;

            case TeachingPointKind.PcbPlaceReference:
                ApplyPlaceReference(recipe, points, point, position);
                break;

            case TeachingPointKind.BoltReference:
                ApplyBoltReference(recipe, points, point, position);
                break;

        }
    }

    private static TeachingPoint Create(
        string name,
        TeachingPointKind kind,
        int station,
        AxisPos position,
        TeachMode mode) => new()
        {
            Name = name,
            Kind = kind,
            Station = station,
            TeachMode = mode,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            IsTaught = position.X != 0 || position.Y != 0 || position.Z != 0,
        };

    private void ApplyPlaceReference(
        Recipe recipe,
        IReadOnlyCollection<TeachingPoint> points,
        TeachingPoint point,
        AxisPos position)
    {
        var target = point.Name == "PcbPlace1"
            ? recipe.PcbPlacement.PcbPlace1
            : recipe.PcbPlacement.PcbPlace2;
        var transformed = config.Calibration.ToPcbPlacement(position);
        target.X = transformed.X;
        target.Y = transformed.Y;

        var linkedPoint = points.Single(candidate =>
            candidate.Name == point.Name
            && candidate.Kind == TeachingPointKind.PcbPlaceZ);
        linkedPoint.X = transformed.X;
        linkedPoint.Y = transformed.Y;
    }

    private void ApplyBoltReference(
        Recipe recipe,
        IReadOnlyCollection<TeachingPoint> points,
        TeachingPoint point,
        AxisPos position)
    {
        var transformed = config.Calibration.ToBoltFastening(position);
        var bolt = recipe.BoltFastening.BoltPoints.Single(candidate => candidate.Name == point.Name);
        bolt.X = transformed.X;
        bolt.Y = transformed.Y;

        var linkedPoint = points.Single(candidate =>
            candidate.Name == point.Name
            && candidate.Kind == TeachingPointKind.BoltZ);
        linkedPoint.X = transformed.X;
        linkedPoint.Y = transformed.Y;
    }
}
