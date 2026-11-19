using System;
using System.Text.Json.Serialization;

namespace IBTM.Core;

public sealed record HeightSample(double X, double Y, double Value);

public sealed record HeightReferencePlane(
    DateTimeOffset RecordedAt, HeightSample Point1, HeightSample Point2, HeightSample Point3)
{
    public double HeightAt(double x, double y)
    {
        var ux = Point2.X - Point1.X;
        var uy = Point2.Y - Point1.Y;
        var vx = Point3.X - Point1.X;
        var vy = Point3.Y - Point1.Y;
        var determinant = GetTriangleDeterminant(ux, uy, vx, vy);
        var dx = x - Point1.X;
        var dy = y - Point1.Y;
        var height = Point1.Value
            + (dx * vy - dy * vx) / determinant * (Point2.Value - Point1.Value)
            + (ux * dy - uy * dx) / determinant * (Point3.Value - Point1.Value);
        if (!double.IsFinite(height))
            throw new InvalidOperationException(UiText.Get("Invalid reference plane measurement"));
        return height;
    }

    public static double GetTriangleDeterminant(double ux, double uy, double vx, double vy)
    {
        var determinant = ux * vy - uy * vx;
        var scale = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        if (!double.IsFinite(scale) || scale == 0 || Math.Abs(determinant) <= scale * 1e-6)
            throw new InvalidOperationException(UiText.Get("Reference points must form a triangle"));
        return determinant;
    }
}

public sealed record BoltHeightResult(
    DateTimeOffset RecordedAt, HeightSample Sample, double ReferenceHeight, double Minimum, double Maximum)
{
    [JsonIgnore]
    public double Height => Sample.Value - ReferenceHeight;
    [JsonIgnore]
    public bool Success => double.IsFinite(Height) && Height >= Minimum && Height <= Maximum;
}
