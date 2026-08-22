using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionStationHardwareSettings : InputHardwareSettings
{
    public override HardwareArea Area => HardwareArea.InspectionStation;

    public InspectionStationHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.InspectionHousing1Present] = 68,
            [InputIo.InspectionHousing2Present] = 69,
        };
    }
}
