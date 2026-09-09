using System.Linq;
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

    public TeachingPosition[] GetTeachingPositions(
        PcbLayout pcb, HeatSinkSlot slot, CarrierReferenceSettings reference) =>
    [
        new(TeachingTarget.SafeZ, MotionGroup.BoltFastening, TeachMode.ZOnly,
            () => new() { Z = SafeZ }, p => SafeZ = p.Z, this),
        new(TeachingTarget.ShootingHeadUpperLeftLocatingPin, MotionGroup.BoltFastening, TeachMode.XYOnly,
            () => ShootingHead.UpperLeftLocatingPin ?? new(), p => ShootingHead.UpperLeftLocatingPin = p,
            this, () => ShootingHead.UpperLeftLocatingPin is not null),
        new(TeachingTarget.ShootingHeadLowerRightLocatingPin, MotionGroup.BoltFastening, TeachMode.XYOnly,
            () => ShootingHead.LowerRightLocatingPin ?? new(), p => ShootingHead.LowerRightLocatingPin = p,
            this, () => ShootingHead.LowerRightLocatingPin is not null),
        .. pcb.GetBolts(slot).Where(bolt => bolt.Head == FasteningHead.Shooting)
            .Select(bolt => GetBoltTeachingPosition(bolt, reference)),
        new(TeachingTarget.PickupHeadUpperLeftLocatingPin, MotionGroup.BoltFastening, TeachMode.XYOnly,
            () => PickupHead.UpperLeftLocatingPin ?? new(), p => PickupHead.UpperLeftLocatingPin = p,
            this, () => PickupHead.UpperLeftLocatingPin is not null),
        new(TeachingTarget.PickupHeadLowerRightLocatingPin, MotionGroup.BoltFastening, TeachMode.XYOnly,
            () => PickupHead.LowerRightLocatingPin ?? new(), p => PickupHead.LowerRightLocatingPin = p,
            this, () => PickupHead.LowerRightLocatingPin is not null),
        new(TeachingTarget.BoltPickup, MotionGroup.BoltFastening, TeachMode.Full,
            () => PickupPosition, p => PickupPosition = p, this),
        .. pcb.GetBolts(slot).Where(bolt => bolt.Head == FasteningHead.Pickup)
            .Select(bolt => GetBoltTeachingPosition(bolt, reference)),
    ];

    private TeachingPosition GetBoltTeachingPosition(BoltTarget bolt, CarrierReferenceSettings reference) =>
        new(
            TeachingTarget.BoltPosition, MotionGroup.BoltFastening, TeachMode.XYOnly,
            () => HasBoltPosition(bolt, reference) ? GetBoltPosition(bolt, reference) : new(),
            null,
            isDefined: () => HasBoltPosition(bolt, reference)) { Bolt = bolt };

    internal bool HasBoltPosition(BoltTarget bolt, CarrierReferenceSettings reference)
    {
        var head = GetHead(bolt.Head);
        return reference.IsDefined
            && CarrierCoordinates.IsDefined(head.UpperLeftLocatingPin, head.LowerRightLocatingPin)
            && bolt is { X: not null, Y: not null };
    }

    public BoltHeadSettings GetHead(FasteningHead head) => head switch
    {
        FasteningHead.Shooting => ShootingHead,
        FasteningHead.Pickup => PickupHead,
        _ => throw new System.ArgumentOutOfRangeException(nameof(head)),
    };

    public AxisPosition GetBoltPosition(
        BoltTarget bolt,
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
        position.Z = SafeZ;
        return position;
    }
}

public sealed class BoltHeadSettings
{
    public AxisPosition? UpperLeftLocatingPin { get; set; }
    public AxisPosition? LowerRightLocatingPin { get; set; }
}
