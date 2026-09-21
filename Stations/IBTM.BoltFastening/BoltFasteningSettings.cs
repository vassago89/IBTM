using System;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningSettings : Setting
{
    public BoltFasteningSettings()
    {
        Motion = new();
        PickupPosition = new();
        ShootingHead = new();
        PickupHead = new();
    }

    public MotionSettings Motion { get; set; }
    public int DryRunMilliseconds
    {
        get;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Dry-run duration must be greater than zero.");
            field = value;
        }
    } = 2_000;
    public int ShootingDetectionTimeoutMilliseconds { get; set; } = 3_000;
    public double ShootingArrivalDelaySeconds
    {
        get;
        set
        {
            if (!double.IsFinite(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), "Shooting arrival delay must be a finite number of 0 seconds or more.");
            }
            field = value;
        }
    } = 3.0;
    public double SafeZ { get; set; }
    public AxisPosition PickupPosition { get; set; }
    public BoltHeadSettings ShootingHead { get; set; }
    public BoltHeadSettings PickupHead { get; set; }

    public TeachingPosition[] GetTeachingPositions(
        PcbLayout pcb,
        HeatSinkSlot slot,
        CarrierReferenceSettings reference)
    {
        return [
            new(
                TeachingTarget.SafeZ,
                MotionGroup.BoltFastening,
                TeachMode.ZOnly,
                () => new() { Z = SafeZ },
                p => SafeZ = p.Z,
                this),
            new(
                TeachingTarget.ShootingHeadFasteningZ,
                MotionGroup.BoltFastening,
                TeachMode.ZOnly,
                () => new() { Z = ShootingHead.FasteningZ },
                p => ShootingHead.FasteningZ = p.Z,
                this),
            new(
                TeachingTarget.ShootingHeadUpperLeftLocatingPin,
                MotionGroup.BoltFastening,
                TeachMode.XYOnly,
                () => ShootingHead.UpperLeftLocatingPin ?? new(),
                p => ShootingHead.UpperLeftLocatingPin = p,
                this,
                () => ShootingHead.UpperLeftLocatingPin is not null),
            new(
                TeachingTarget.ShootingHeadLowerRightLocatingPin,
                MotionGroup.BoltFastening,
                TeachMode.XYOnly,
                () => ShootingHead.LowerRightLocatingPin ?? new(),
                p => ShootingHead.LowerRightLocatingPin = p,
                this,
                () => ShootingHead.LowerRightLocatingPin is not null),
            ..pcb.GetBolts(slot)
                .Where(bolt => bolt.Head == FasteningHead.Shooting)
                .Select(bolt => GetBoltTeachingPosition(bolt, reference)),
            new(
                TeachingTarget.PickupHeadFasteningZ,
                MotionGroup.BoltFastening,
                TeachMode.ZOnly,
                () => new() { Z = PickupHead.FasteningZ },
                p => PickupHead.FasteningZ = p.Z,
                this),
            new(
                TeachingTarget.PickupHeadUpperLeftLocatingPin,
                MotionGroup.BoltFastening,
                TeachMode.XYOnly,
                () => PickupHead.UpperLeftLocatingPin ?? new(),
                p => PickupHead.UpperLeftLocatingPin = p,
                this,
                () => PickupHead.UpperLeftLocatingPin is not null),
            new(
                TeachingTarget.PickupHeadLowerRightLocatingPin,
                MotionGroup.BoltFastening,
                TeachMode.XYOnly,
                () => PickupHead.LowerRightLocatingPin ?? new(),
                p => PickupHead.LowerRightLocatingPin = p,
                this,
                () => PickupHead.LowerRightLocatingPin is not null),
            new(
                TeachingTarget.BoltPickup,
                MotionGroup.BoltFastening,
                TeachMode.Full,
                () => PickupPosition,
                p => PickupPosition = p,
                this),
            ..pcb.GetBolts(slot)
                .Where(bolt => bolt.Head == FasteningHead.Pickup)
                .Select(bolt => GetBoltTeachingPosition(bolt, reference)),
        ];
    }

    private TeachingPosition GetBoltTeachingPosition(BoltPoint bolt, CarrierReferenceSettings reference)
    {
        return new(
            TeachingTarget.BoltPosition,
            MotionGroup.BoltFastening,
            TeachMode.Full,
            () => HasBoltPosition(bolt, reference) ? GetBoltPosition(bolt, reference) : new(),
            null,
            isDefined: () => HasBoltPosition(bolt, reference))
        { Bolt = bolt };
    }

    internal bool HasBoltPosition(BoltPoint bolt, CarrierReferenceSettings reference)
    {
        var head = GetHead(bolt.Head);
        return reference.IsDefined
            && CarrierCoordinates.IsDefined(head.UpperLeftLocatingPin, head.LowerRightLocatingPin)
            && bolt is { X: not null, Y: not null };
    }

    public BoltHeadSettings GetHead(FasteningHead head)
    {
        switch (head)
        {
            case FasteningHead.Shooting:
                return ShootingHead;
            case FasteningHead.Pickup:
                return PickupHead;
            default:
                throw new System.ArgumentOutOfRangeException(nameof(head));
        }
    }

    public AxisPosition GetBoltPosition(BoltPoint bolt, CarrierReferenceSettings reference)
    {
        var head = GetHead(bolt.Head);
        var position = CarrierCoordinates.ToMachine(
            new AxisPosition { X = bolt.X!.Value, Y = bolt.Y!.Value, },
            reference.UpperLeftLocatingPin!,
            reference.LowerRightLocatingPin!,
            head.UpperLeftLocatingPin!,
            head.LowerRightLocatingPin!);
        position.Z = head.FasteningZ;
        return position;
    }
}

public sealed class BoltHeadSettings
{
    public double FasteningZ { get; set; }
    public AxisPosition? UpperLeftLocatingPin { get; set; }
    public AxisPosition? LowerRightLocatingPin { get; set; }
}
