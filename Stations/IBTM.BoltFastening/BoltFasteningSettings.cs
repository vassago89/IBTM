using System;
using System.Text.Json.Serialization;
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
        MonitorTorqueCurves = true;
        TorqueCurveSampling = AdcTorqueCurveSampling.Milliseconds30;
        TorqueCurveTimeoutMilliseconds = 1_000;
        TorqueCurveStopTimeoutMilliseconds = 3_000;
    }

    public MotionSettings Motion { get; set; }
    // Unset preserves the existing Z motion times.
    public double? FeedAccelerationSeconds
    {
        get;
        set
        {
            if (value is { } seconds && (!double.IsFinite(seconds) || seconds <= 0))
                throw new ArgumentOutOfRangeException(nameof(value), "Use a positive finite acceleration time.");
            field = value;
        }
    }
    public double? FeedDecelerationSeconds
    {
        get;
        set
        {
            if (value is { } seconds && (!double.IsFinite(seconds) || seconds <= 0))
                throw new ArgumentOutOfRangeException(nameof(value), "Use a positive finite deceleration time.");
            field = value;
        }
    }
    public FasteningHead FirstFasteningHead
    {
        get;
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    }
    public ushort PickupPreliminaryPreset
    {
        get;
        set
        {
            if (value is < 1 or > 3)
                throw new ArgumentOutOfRangeException(nameof(value), "IO bolt presets are 1, 2 and 3.");
            field = value;
        }
    } = 2;
    [JsonPropertyName("PickupFinalPreset")]
    public ushort ShootingIpmPreset
    {
        get;
        set
        {
            if (value is < 1 or > 3)
                throw new ArgumentOutOfRangeException(nameof(value), "IO bolt presets are 1, 2 and 3.");
            field = value;
        }
    } = 1;

    public ushort ShootingPcbPreset
    {
        get;
        set
        {
            if (value is < 1 or > 3)
                throw new ArgumentOutOfRangeException(nameof(value), "IO bolt presets are 1, 2 and 3.");
            field = value;
        }
    } = 1;

    public bool MonitorTorqueCurves { get; set; }
    public AdcTorqueCurveSampling TorqueCurveSampling
    {
        get;
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    }
    public int TorqueCurveTimeoutMilliseconds
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    }
    public int TorqueCurveStopTimeoutMilliseconds
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    }

    public int PickupVacuumDelayMilliseconds
    {
        get;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Pickup vacuum delay must be zero or greater.");
            field = value;
        }
    } = 100;
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
    // Unset uses the existing travel height until the shooting height is taught.
    public double? ShootingSafeZ
    {
        get;
        set
        {
            if (value is { } z && !double.IsFinite(z))
                throw new ArgumentOutOfRangeException(nameof(value), "Use a finite shooting Safe Z.");
            field = value;
        }
    }

    public AxisPosition PickupPosition { get; set; }
    public BoltHeadSettings ShootingHead { get; set; }
    public BoltHeadSettings PickupHead { get; set; }

    public double GetSafeZ(FasteningHead head)
    {
        return head == FasteningHead.Shooting ? ShootingSafeZ ?? SafeZ : SafeZ;
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

    public AxisPosition GetBoltPosition(BoltPoint bolt, bool final = false)
    {
        if (final)
        {
            if (bolt.FinalFasteningPosition is not { } position
                || !double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
                throw new InvalidOperationException($"Teach shooting final XYZ for {bolt.HeatSink}, bolt {bolt.Id} before moving.");
            return new() { X = position.X, Y = position.Y, Z = position.Z };
        }
        if (!bolt.IsFasteningPositionDefined)
            throw new InvalidOperationException($"Record fastening XY for {bolt.HeatSink}, bolt {bolt.Id} before moving.");
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
