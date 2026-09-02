using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningSettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public double SafeZ { get; set; }
    public AxisPosition PickupPosition { get; set; } = new();
    public BoltHeadSettings ShootingHead { get; set; } = new();
    public BoltHeadSettings PickupHead { get; set; } = new();

    public BoltHeadSettings GetHead(FasteningHead head) => head switch
    {
        FasteningHead.Shooting => ShootingHead,
        FasteningHead.Pickup => PickupHead,
        _ => throw new System.ArgumentOutOfRangeException(nameof(head)),
    };

    public AxisPosition GetBoltPosition(
        BoltPoint bolt,
        CarrierReferenceSettings reference)
    {
        var head = GetHead(bolt.Head);
        var position = CarrierCoordinates.ToMachine(
            new AxisPosition
            {
                X = bolt.X!.Value,
                Y = bolt.Y!.Value,
            },
            reference.UpperLeftLocatingPin!,
            reference.LowerRightLocatingPin!,
            head.UpperLeftLocatingPin!,
            head.LowerRightLocatingPin!);
        position.Z = bolt.Z!.Value;
        return position;
    }
}

public sealed class BoltHeadSettings
{
    public AxisPosition? UpperLeftLocatingPin { get; set; }
    public AxisPosition? LowerRightLocatingPin { get; set; }
}
