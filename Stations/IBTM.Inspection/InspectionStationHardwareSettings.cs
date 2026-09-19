using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionStationHardwareSettings : InputHardwareSettings
{
    public InspectionStationHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.InspectionHeatSink1Present] = 69,
            [InputIo.InspectionHeatSink2Present] = 70,
        };
    }

    public override HardwareArea Area => HardwareArea.InspectionStation;
}
