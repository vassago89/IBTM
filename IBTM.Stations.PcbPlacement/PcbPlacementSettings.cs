using IBTM.Core;
using IBTM.Device;

namespace IBTM.Stations.PcbPlacement;

public sealed class PcbPlacementSettings
{
    public StationMotionSettings Motion { get; set; } = new();
    public AxisPos HandoffPickPosition { get; set; } = new() { X = 100.0, Y = 60.0, Z = 25.0 };
    public double AlignmentXMillimetersPerPixel { get; set; } = 0.02;
    public double AlignmentYMillimetersPerPixel { get; set; } = 0.02;
}
