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

}
