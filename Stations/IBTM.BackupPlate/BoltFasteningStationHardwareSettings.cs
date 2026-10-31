using System.Text.Json.Serialization;
using IBTM.Device;

namespace IBTM.BackupPlate;

public sealed class BoltFasteningStationHardwareSettings : IoHardwareSettings
{
    public BoltFasteningStationHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.BoltFasteningHeatSink1Present] = 62,
            [InputIo.BoltFasteningHeatSink2Present] = 63,
            [InputIo.BoltFasteningStopperDown] = 64,
            [InputIo.BoltFasteningStopperUp] = 65,
            [InputIo.BoltFasteningBackupPlateUp] = 66,
            [InputIo.BoltFasteningBackupPlateDown] = 67,
        };
        Outputs = new()
        {
            [OutputIo.BoltFasteningStopperUp] = CreateOutput(
                52, 53, InputIo.BoltFasteningStopperUp, InputIo.BoltFasteningStopperDown),
            [OutputIo.BoltFasteningBackupPlateUp] = CreateOutput(
                54, 55, InputIo.BoltFasteningBackupPlateUp, InputIo.BoltFasteningBackupPlateDown),
        };
    }

    [JsonIgnore]
    public override HardwareArea Area => HardwareArea.BoltFasteningStation;
}
