using IBTM.Core;
using IBTM.Device;

namespace IBTM.Stations.PcbPlacement;

public sealed class PcbPlacementSettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public AxisPos BufferPosition { get; set; } = new() { X = 10.0, Y = 8.0, Z = 16.0 };
    public double AlignmentXMillimetersPerPixel { get; set; } = 0.02;
    public double AlignmentYMillimetersPerPixel { get; set; } = 0.02;
}
