using System;
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
    public int PickupRetryCount
    {
        get;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Pickup retry count must be zero or greater.");
            field = value;
        }
    } = 3;
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

    public void InitializeBoltPosition(BoltPoint bolt, CarrierReferenceSettings reference)
    {
        if (bolt.FasteningX is not null || bolt.FasteningY is not null
            || bolt is not { X: { } x, Y: { } y }
            || !double.IsFinite(x) || !double.IsFinite(y))
            return;
        var head = GetHead(bolt.Head);
        if (!reference.IsDefined
            || !CarrierCoordinates.IsDefined(head.UpperLeftLocatingPin, head.LowerRightLocatingPin))
            return;
        var position = CarrierCoordinates.ToMachine(
            new AxisPosition { X = x, Y = y },
            reference.UpperLeftLocatingPin!,
            reference.LowerRightLocatingPin!,
            head.UpperLeftLocatingPin!,
            head.LowerRightLocatingPin!);
        bolt.FasteningX = position.X;
        bolt.FasteningY = position.Y;
    }

    public AxisPosition GetBoltPosition(BoltPoint bolt)
    {
        if (!bolt.IsFasteningPositionDefined)
            throw new InvalidOperationException($"Record fastening XY for {bolt.HeatSink}, bolt {bolt.Number} before moving.");
        return new()
        {
            X = bolt.FasteningX!.Value,
            Y = bolt.FasteningY!.Value,
            Z = GetHead(bolt.Head).FasteningZ + bolt.FasteningZOffset,
        };
    }
}

public sealed class BoltHeadSettings
{
    public double FasteningZ { get; set; }
    public AxisPosition? UpperLeftLocatingPin { get; set; }
    public AxisPosition? LowerRightLocatingPin { get; set; }
}
