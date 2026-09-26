using System.Text.Json.Serialization;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningStationHardwareSettings : InputHardwareSettings
{
    public BoltFasteningStationHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.BoltFasteningHeatSink1Present] = 62,
            [InputIo.BoltFasteningHeatSink2Present] = 63,
        };
    }

    [JsonIgnore]
    public override HardwareArea Area => HardwareArea.BoltFasteningStation;
}
