using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace IBTM.Core;

[JsonConverter(typeof(JsonStringEnumConverter<FasteningHead>))]
public enum FasteningHead
{
    [Description("Shooting")]
    Shooting,

    [Description("Pickup")]
    Pickup,
}

public sealed class BoltPoint
{
    public BoltPoint() : this(Guid.NewGuid())
    {
    }

    [JsonConstructor]
    public BoltPoint(Guid id)
    {
        Id = id;
    }

    public Guid Id { get; init; }
    public string? Name { get; set; }

    public static string GetDisplayName(string? name, int? ordinal)
    {
        return !string.IsNullOrWhiteSpace(name) ? name
            : ordinal is { } number ? UiText.Format($"Bolt {number}") : UiText.Get("Unnamed bolt");
    }

    public int? LightLevel
    {
        get;
        set
        {
            if (value is < 0 or > 255)
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Use 0 to 255, or leave blank for the recipe default."));
            field = value;
        }
    }

    public double? MinimumHeight
    {
        get;
        set
        {
            if (value is { } height && !double.IsFinite(height))
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Use a finite height in millimetres."));
            field = value;
        }
    }

    public double? MaximumHeight
    {
        get;
        set
        {
            if (value is { } height && !double.IsFinite(height))
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Use a finite height in millimetres."));
            field = value;
        }
    }
    public (double Minimum, double Maximum) GetHeightLimits(int? ordinal)
    {
        if (MinimumHeight is { } min && MaximumHeight is { } max && min <= max)
            return (min, max);
        throw new InvalidOperationException(UiText.Format($"Set the height limits for {GetDisplayName(Name, ordinal)}"));
    }

    public HeatSinkSlot HeatSink { get; set; }
    public FasteningHead Head { get; set; } = FasteningHead.Shooting;
    // Inspection camera XY in the recipe reference frame, in millimetres.
    public double? X { get; set; }
    public double? Y { get; set; }

    [JsonIgnore]
    public AxisPosition? InspectionPosition
    {
        get
        {
            return X is { } x && Y is { } y
                && double.IsFinite(x) && double.IsFinite(y) ? new() { X = x, Y = y } : null;
        }
    }

    // Taught independently at the fastening station.
    public double? FasteningX { get; set; }
    public double? FasteningY { get; set; }
    [JsonIgnore]
    public bool IsFasteningPositionDefined
    {
        get
        {
            return FasteningX is { } x && double.IsFinite(x)
                && FasteningY is { } y && double.IsFinite(y);
        }
    }

    public double FasteningZOffset
    {
        get;
        set
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Use a finite Z offset in millimetres."));
            field = value;
        }
    }

    public double FeedDistance
    {
        get;
        set
        {
            if (!double.IsFinite(value) || value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Use a finite distance of zero or greater."));
            field = value;
        }
    }

    public double FeedSeconds
    {
        get;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Use a positive finite time in seconds."));
            field = value;
        }
    } = 1;

    // XY only; Z is the shooting preliminary reference plus the per-bolt offset.
    [JsonPropertyName("FinalFasteningPosition")]
    public AxisPosition? PreliminaryFasteningPosition { get; set; }

    public double PreliminaryFasteningZOffset
    {
        get;
        set
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Use a finite Z offset in millimetres."));
            field = value;
        }
    }

    [JsonPropertyName("FinalFeedDistance")]
    public double PreliminaryFeedDistance
    {
        get;
        set
        {
            if (!double.IsFinite(value) || value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Use a finite distance of zero or greater."));
            field = value;
        }
    }

    [JsonPropertyName("FinalFeedSeconds")]
    public double PreliminaryFeedSeconds
    {
        get;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Use a positive finite time in seconds."));
            field = value;
        }
    } = 1;
}
