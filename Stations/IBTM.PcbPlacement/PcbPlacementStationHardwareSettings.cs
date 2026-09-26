using System.Text.Json.Serialization;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementStationHardwareSettings : InputHardwareSettings
{
    public PcbPlacementStationHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.PcbPlacementHeatSink1Present] = 54,
            [InputIo.PcbPlacementHeatSink2Present] = 55,
        };
    }

    [JsonIgnore]
    public override HardwareArea Area => HardwareArea.PcbPlacementStation;
}
