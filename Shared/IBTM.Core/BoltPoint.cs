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
    public int Number { get; set; }
    public HeatSinkSlot HeatSink { get; set; }
    public FasteningHead Head { get; set; } = FasteningHead.Shooting;
    // Actual Inspection Gantry XY captured by Record Position, in millimetres.
    public double? X { get; set; }
    public double? Y { get; set; }

    // Null retains the inspection defaults of recipes saved before per-bolt settings.
    public int? BrightnessThreshold
    {
        get;
        set
        {
            if (value is < 0 or > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 255.");
            field = value;
        }
    }

    public double? MinimumBrightRatio
    {
        get;
        set
        {
            if (value is { } ratio && !(ratio >= 0 && ratio <= 1))
                throw new ArgumentOutOfRangeException(nameof(value), "Use a ratio from 0 to 1.");
            field = value;
        }
    }
}
