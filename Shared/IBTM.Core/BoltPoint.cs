using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace IBTM.Core;

[JsonConverter(typeof(JsonStringEnumConverter<FasteningHead>))]
public enum FasteningHead
{
    [Description("Head 2 Shooting")]
    Shooting,

    [Description("Head 1 Pickup")]
    Pickup,
}

public sealed class BoltPoint
{
    private int? _brightnessThreshold;
    private double? _minimumBrightRatio;

    public int Number { get; set; }
    public HeatSinkSlot HeatSink { get; set; }
    public FasteningHead Head { get; set; } = FasteningHead.Shooting;
    public double? X { get; set; }
    public double? Y { get; set; }

    // Null retains the inspection defaults of recipes saved before per-bolt settings.
    public int? BrightnessThreshold
    {
        get
        {
            return _brightnessThreshold;
        }
        set
        {
            if (value is < 0 or > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 255.");
            _brightnessThreshold = value;
        }
    }

    public double? MinimumBrightRatio
    {
        get
        {
            return _minimumBrightRatio;
        }
        set
        {
            if (value is { } ratio && !(ratio >= 0 && ratio <= 1))
                throw new ArgumentOutOfRangeException(nameof(value), "Use a ratio from 0 to 1.");
            _minimumBrightRatio = value;
        }
    }
}
