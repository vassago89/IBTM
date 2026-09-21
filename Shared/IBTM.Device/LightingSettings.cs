using System;
using System.ComponentModel;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM.Device;

[JsonConverter(typeof(JsonStringEnumConverter<LightDriver>))]
public enum LightDriver
{
    [Description("Virtual")]
    Virtual,

    [Description("MOVS (Serial)")]
    Movs,
}

public sealed class LightingSettings : Setting
{
    // Retain the persisted key used by existing COM port settings.
    public string Connection { get; set; } = string.Empty;
    public int InspectionChannel { get; set; } = 2;
    public int StabilizationDelayMilliseconds
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 100;
}
