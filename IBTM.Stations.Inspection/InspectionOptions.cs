using IBTM.Core.Machine;

namespace IBTM.Stations.Inspection;

public sealed class InspectionOptions
{
    public StationMotionSettings Motion { get; set; } = new();
    public int NgStackMaxCount { get; set; } = 3;
    public double PixelsPerMm { get; set; } = 50.0;
}
