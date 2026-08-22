using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningStationHardwareSettings : InputHardwareSettings
{
    public override HardwareArea Area => HardwareArea.BoltFasteningStation;

    public BoltFasteningStationHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.BoltFasteningHousing1Present] = 61,
            [InputIo.BoltFasteningHousing2Present] = 62,
        };
    }
}
