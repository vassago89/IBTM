using System;
using System.Text.Json.Serialization;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandlerSettings : Setting
{
    public PcbPlacementHandlerSettings()
    {
        Motion = new();
        HandoffPosition = new();
    }

    public MotionSettings Motion { get; set; }
    [JsonPropertyName("BufferHandoffPosition")]
    public AxisPosition HandoffPosition { get; set; }
    public double? ReceiveZ { get; set; }

    public double IpmDelaySeconds
    {
        get;
        set
        {
            if (!double.IsFinite(value)
                || value < 0
                || value > int.MaxValue / 1000.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), "IPM delay must be non-negative and fit in milliseconds.");
            }

            field = value;
        }
    }
}
