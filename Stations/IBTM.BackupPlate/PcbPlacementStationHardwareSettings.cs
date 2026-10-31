using System.Text.Json.Serialization;
using IBTM.Device;

namespace IBTM.BackupPlate;

public sealed class PcbPlacementStationHardwareSettings : IoHardwareSettings
{
    public PcbPlacementStationHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.PcbPlacementHeatSink1Present] = 54,
            [InputIo.PcbPlacementHeatSink2Present] = 55,
            [InputIo.PcbPlacementStopperDown] = 57,
            [InputIo.PcbPlacementStopperUp] = 58,
            [InputIo.PcbPlacementBackupPlateUp] = 59,
            [InputIo.PcbPlacementBackupPlateDown] = 60,
        };
        Outputs = new()
        {
            [OutputIo.PcbPlacementStopperUp] = CreateOutput(
                48, 49, InputIo.PcbPlacementStopperUp, InputIo.PcbPlacementStopperDown),
            [OutputIo.PcbPlacementBackupPlateUp] = CreateOutput(
                50, 51, InputIo.PcbPlacementBackupPlateUp, InputIo.PcbPlacementBackupPlateDown),
        };
    }

    [JsonIgnore]
    public override HardwareArea Area => HardwareArea.PcbPlacementStation;
}
