using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementStationHardwareSettings : InputHardwareSettings
{
    public override HardwareArea Area => HardwareArea.PcbPlacementStation;

    public PcbPlacementStationHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.PcbPlacementHousing1Present] = 54,
            [InputIo.PcbPlacementHousing2Present] = 55,
        };
    }
}
