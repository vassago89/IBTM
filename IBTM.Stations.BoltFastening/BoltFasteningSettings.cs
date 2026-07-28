using IBTM.Device;

namespace IBTM.Stations.BoltFastening;

public sealed class BoltFasteningSettings
{
    public StationMotionSettings Motion { get; set; } = new();
    public BoltHeadSettings StandardHead { get; set; } = new();
    public BoltHeadSettings LoctiteHead { get; set; } = new();
    public int RetryCount { get; set; } = 2;

    public BoltHeadSettings GetHead(BoltType boltType) => boltType switch
    {
        BoltType.Standard => StandardHead,
        BoltType.Loctite => LoctiteHead,
        _ => throw new System.ArgumentOutOfRangeException(nameof(boltType)),
    };
}

public sealed class BoltHeadSettings
{
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double DefaultTorqueNm { get; set; } = 15.0;
}
