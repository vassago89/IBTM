using IBTM.Core;
using IBTM.Device;

namespace IBTM.Stations.Inspection;

public sealed class InspectionSettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public int NgCarrierCapacity { get; set; } = 3;
    public double PixelsPerMm { get; set; } = 50.0;
}
