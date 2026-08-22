using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningSettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public AxisPos PickupPosition { get; set; } = new();
    public BoltHeadSettings ShootingHead { get; set; } = new();
    public BoltHeadSettings PickupHead { get; set; } = new();

    public BoltHeadSettings GetHead(FasteningHead head) => head switch
    {
        FasteningHead.Shooting => ShootingHead,
        FasteningHead.Pickup => PickupHead,
        _ => throw new System.ArgumentOutOfRangeException(nameof(head)),
    };

    public AxisPos GetBoltPosition(BoltPoint bolt)
    {
        var head = GetHead(bolt.Head);
        var position = CarrierCoordinates.ToMachine(
            new AxisPos
            {
                X = bolt.X!.Value,
                Y = bolt.Y!.Value,
            },
            head.UpperLeftLocatingPin!,
            head.LowerRightLocatingPin!);
        position.Z = bolt.Z;
        return position;
    }
}

public sealed class BoltHeadSettings
{
    public AxisPos? UpperLeftLocatingPin { get; set; }
    public AxisPos? LowerRightLocatingPin { get; set; }
}
