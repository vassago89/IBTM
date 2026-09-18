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

    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.BoltFasteningStation;
        }
    }
}
