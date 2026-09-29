using System;
using IBTM.Core;

namespace IBTM.Device;

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
