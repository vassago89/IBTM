using System.Text.Json.Serialization;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplySettings : Setting
{
    public PcbSupplySettings()
    {
        Motion = new();
        HandoffPosition = new();
    }

    public MotionSettings Motion { get; set; }
    public double RotationZ { get; set; }
    [JsonPropertyName("BufferHandoffPosition")]
    public AxisPosition HandoffPosition { get; set; }

}
