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

    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.PcbPlacementStation;
        }
    }
}
