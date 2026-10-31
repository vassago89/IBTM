using System.Text.Json.Serialization;
using IBTM.Device;

namespace IBTM.BackupPlate;

public sealed class InspectionStationHardwareSettings : IoHardwareSettings
{
    public InspectionStationHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.InspectionHeatSink1Present] = 69,
            [InputIo.InspectionHeatSink2Present] = 70,
            [InputIo.InspectionStopperDown] = 71,
            [InputIo.InspectionStopperUp] = 72,
            [InputIo.InspectionBackupPlateUp] = 73,
            [InputIo.InspectionBackupPlateDown] = 74,
        };
        Outputs = new()
        {
            [OutputIo.InspectionStopperUp] = CreateOutput(
                56, 57, InputIo.InspectionStopperUp, InputIo.InspectionStopperDown),
            [OutputIo.InspectionBackupPlateUp] = CreateOutput(
                58, 59, InputIo.InspectionBackupPlateUp, InputIo.InspectionBackupPlateDown),
        };
    }

    [JsonIgnore]
    public override HardwareArea Area => HardwareArea.InspectionStation;
}
