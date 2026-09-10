using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionStationHardwareSettings : InputHardwareSettings
{
    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.InspectionStation;
        }
    }

    public InspectionStationHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.InspectionHeatSink1Present] = 68,
            [InputIo.InspectionHeatSink2Present] = 69,
        };
    }
}
