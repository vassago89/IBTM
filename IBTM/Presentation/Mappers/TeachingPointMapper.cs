
namespace IBTM.Presentation.Mappers;

/// <summary>Maps recipe coordinates to editable teaching points and back.</summary>
public sealed class TeachingPointMapper(MachineConfig config)
{
    public List<TeachingPoint> Build(Recipe recipe)
    {
        var points = new List<TeachingPoint>
        {
            Create("PcbPick1", TeachingPointKind.Zone1PcbPick, 1, recipe.Zone1_PcbPick1, TeachMode.Full),
            Create("PcbPick2", TeachingPointKind.Zone1PcbPick, 1, recipe.Zone1_PcbPick2, TeachMode.Full),
            Create("PcbPlace1", TeachingPointKind.Zone1PcbPlaceZ, 1, recipe.Zone1_PcbPlace1, TeachMode.ZOnly),
            Create("PcbPlace2", TeachingPointKind.Zone1PcbPlaceZ, 1, recipe.Zone1_PcbPlace2, TeachMode.ZOnly),
            Create("Fiducial", TeachingPointKind.Zone2Fiducial, 2, recipe.Zone2_FiducialPos, TeachMode.Full),
        };

        points.AddRange(recipe.BoltPoints.Select(bolt => new TeachingPoint
        {
            Name = bolt.Name,
            Kind = TeachingPointKind.Zone2BoltZ,
            Zone = 2,
            TeachMode = TeachMode.ZOnly,
            X = bolt.X,
            Y = bolt.Y,
            Z = bolt.Z,
            IsTaught = bolt.Z != 0,
            TargetTorqueNm = bolt.TargetTorqueNm,
        }));

        points.Add(Create(
            "InspectPos",
            TeachingPointKind.Zone3Inspection,
            3,
            recipe.Zone3_InspectPos,
            TeachMode.Full));
        points.Add(Create(
            "NgPickup",
            TeachingPointKind.Zone3NgPickup,
            3,
            recipe.Zone3_NgPickupPos,
            TeachMode.Full));
        points.Add(Create(
            "NgPlace",
            TeachingPointKind.Zone3NgPlace,
            3,
            recipe.Zone3_NgPlacePos,
            TeachMode.Full));
        points.Add(Create(
            "PcbPlace1",
            TeachingPointKind.Zone3PlaceReference,
            3,
            config.FromZone1ToZone3(recipe.Zone1_PcbPlace1),
            TeachMode.XYOnly));
        points.Add(Create(
            "PcbPlace2",
            TeachingPointKind.Zone3PlaceReference,
            3,
            config.FromZone1ToZone3(recipe.Zone1_PcbPlace2),
            TeachMode.XYOnly));

        points.AddRange(recipe.BoltPoints.Select(bolt =>
        {
            var position = config.FromZone2ToZone3(new AxisPos { X = bolt.X, Y = bolt.Y });
            return new TeachingPoint
            {
                Name = bolt.Name,
                Kind = TeachingPointKind.Zone3BoltReference,
                Zone = 3,
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
            case TeachingPointKind.Zone1PcbPick:
                if (point.Name == "PcbPick1")
                {
                    recipe.Zone1_PcbPick1 = position;
                }
                else
                {
                    recipe.Zone1_PcbPick2 = position;
                }
                break;

            case TeachingPointKind.Zone1PcbPlaceZ:
                var place = point.Name == "PcbPlace1"
                    ? recipe.Zone1_PcbPlace1
                    : recipe.Zone1_PcbPlace2;
                place.Z = point.Z;
                break;

            case TeachingPointKind.Zone2Fiducial:
                recipe.Zone2_FiducialPos = position;
                break;

            case TeachingPointKind.Zone2BoltZ:
                recipe.BoltPoints.Single(bolt => bolt.Name == point.Name).Z = point.Z;
                break;

            case TeachingPointKind.Zone3Inspection:
                recipe.Zone3_InspectPos = position;
                break;

            case TeachingPointKind.Zone3NgPickup:
                recipe.Zone3_NgPickupPos = position;
                break;

            case TeachingPointKind.Zone3NgPlace:
                recipe.Zone3_NgPlacePos = position;
                break;

            case TeachingPointKind.Zone3PlaceReference:
                ApplyPlaceReference(recipe, points, point, position);
                break;

            case TeachingPointKind.Zone3BoltReference:
                ApplyBoltReference(recipe, points, point, position);
                break;
        }
    }

    private static TeachingPoint Create(
        string name,
        TeachingPointKind kind,
        int zone,
        AxisPos position,
        TeachMode mode) => new()
        {
            Name = name,
            Kind = kind,
            Zone = zone,
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
            ? recipe.Zone1_PcbPlace1
            : recipe.Zone1_PcbPlace2;
        var transformed = config.ToZone1(position);
        target.X = transformed.X;
        target.Y = transformed.Y;

        var linkedPoint = points.Single(candidate =>
            candidate.Name == point.Name
            && candidate.Kind == TeachingPointKind.Zone1PcbPlaceZ);
        linkedPoint.X = transformed.X;
        linkedPoint.Y = transformed.Y;
    }

    private void ApplyBoltReference(
        Recipe recipe,
        IReadOnlyCollection<TeachingPoint> points,
        TeachingPoint point,
        AxisPos position)
    {
        var transformed = config.ToZone2(position);
        var bolt = recipe.BoltPoints.Single(candidate => candidate.Name == point.Name);
        bolt.X = transformed.X;
        bolt.Y = transformed.Y;

        var linkedPoint = points.Single(candidate =>
            candidate.Name == point.Name
            && candidate.Kind == TeachingPointKind.Zone2BoltZ);
        linkedPoint.X = transformed.X;
        linkedPoint.Y = transformed.Y;
    }
}
