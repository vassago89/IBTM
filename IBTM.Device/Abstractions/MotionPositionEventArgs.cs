namespace IBTM.Device.Abstractions;

/// <summary>A three-axis position in millimetres. A missing value means the axis is not configured.</summary>
public readonly record struct MotionPosition(double? X, double? Y, double? Z);

/// <summary>Position update raised while a motion is in progress.</summary>
public sealed class MotionPositionEventArgs(double x, double y, double z) : EventArgs
{
    public double X { get; } = x;
    public double Y { get; } = y;
    public double Z { get; } = z;
}
