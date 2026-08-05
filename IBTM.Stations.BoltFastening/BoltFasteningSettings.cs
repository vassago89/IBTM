using IBTM.Core;
using IBTM.Device;

namespace IBTM.Stations.BoltFastening;

public sealed class BoltFasteningSettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public AxisPos LoctitePickupPosition { get; set; } = new();
    public BoltHeadSettings StandardHead { get; set; } = new();
    public BoltHeadSettings LoctiteHead { get; set; } = new();

    public BoltHeadSettings GetHead(FasteningHead head) => head switch
    {
        FasteningHead.Standard => StandardHead,
        FasteningHead.Loctite => LoctiteHead,
        _ => throw new System.ArgumentOutOfRangeException(nameof(head)),
    };
}

public sealed class BoltHeadSettings
{
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double DefaultTorqueNm { get; set; } = 15.0;
}
