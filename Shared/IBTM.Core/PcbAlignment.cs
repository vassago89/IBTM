using System;
using System.Text.Json.Serialization;

namespace IBTM.Core;

public sealed record Point2D(double X, double Y);

public sealed record FiducialObservation(Point2D Reference, Point2D Measured, double ShapeError, double? MatchScore = null);

// Measured for one PCB in one inspection run. Recipe coordinates remain unchanged.
public sealed record PcbAlignment(DateTimeOffset RecordedAt, FiducialObservation First, FiducialObservation Second)
{
    [JsonIgnore]
    public double AngleDegrees
    {
        get
        {
            var x = Second.Reference.X - First.Reference.X;
            var y = Second.Reference.Y - First.Reference.Y;
            var dx = Second.Measured.X - First.Measured.X;
            var dy = Second.Measured.Y - First.Measured.Y;
            return Math.Atan2(x * dy - y * dx, x * dx + y * dy) * 180 / Math.PI;
        }
    }

    public AxisPosition Transform(AxisPosition position, bool inverse = false)
    {
        var from = (X: (First.Reference.X + Second.Reference.X) / 2, Y: (First.Reference.Y + Second.Reference.Y) / 2);
        var to = (X: (First.Measured.X + Second.Measured.X) / 2, Y: (First.Measured.Y + Second.Measured.Y) / 2);
        var angle = AngleDegrees * Math.PI / 180;
        if (inverse)
        {
            (from, to) = (to, from);
            angle = -angle;
        }
        var (sin, cos) = Math.SinCos(angle);
        var x = position.X - from.X;
        var y = position.Y - from.Y;
        return new()
        {
            X = to.X + x * cos - y * sin,
            Y = to.Y + x * sin + y * cos,
            Z = position.Z,
        };
    }
}
