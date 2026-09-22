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
    public int? LightLevel
    {
        get;
        set
        {
            if (value is < 0 or > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 255, or leave blank for the recipe default.");
            field = value;
        }
    }
    public int Number { get; set; }
    public HeatSinkSlot HeatSink { get; set; }
    public FasteningHead Head { get; set; } = FasteningHead.Shooting;
    // Actual Inspection Gantry XY captured by Record Position, in millimetres.
    public double? X { get; set; }
    public double? Y { get; set; }

    // Seeded from inspection once, then taught independently at the fastening station.
    public double? FasteningX { get; set; }
    public double? FasteningY { get; set; }
    [JsonIgnore]
    public bool IsFasteningPositionDefined => FasteningX is { } x && double.IsFinite(x)
        && FasteningY is { } y && double.IsFinite(y);

    public double FasteningZOffset
    {
        get;
        set
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), "Use a finite Z offset in millimetres.");
            field = value;
        }
    }

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
