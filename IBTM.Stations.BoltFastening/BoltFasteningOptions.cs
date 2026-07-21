using IBTM.Core.Machine;

namespace IBTM.Stations.BoltFastening;

public sealed class BoltFasteningOptions
{
    public ZoneMotionParams Motion { get; set; } = new();
    public double DefaultTorqueNm { get; set; } = 15.0;
    public int RetryCount { get; set; } = 2;
    public double PixelsPerMm { get; set; } = 50.0;
}
