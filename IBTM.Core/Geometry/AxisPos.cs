namespace IBTM.Core.Geometry;

public sealed class AxisPos
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public AxisPos Clone() => new() { X = X, Y = Y, Z = Z };
}
