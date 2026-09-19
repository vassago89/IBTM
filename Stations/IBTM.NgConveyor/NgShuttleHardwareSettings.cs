using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgShuttleHardwareSettings : IoHardwareSettings
{
    public NgShuttleHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.NgShuttleDown] = 80,
            [InputIo.NgShuttleUp] = 81,
            [InputIo.NgShuttleCarrierDetected] = 82,
        };
        Outputs = new()
        {
            [OutputIo.NgShuttleDown] = CreateOutput(68, 69, InputIo.NgShuttleDown, InputIo.NgShuttleUp),
        };
    }

    public override HardwareArea Area => HardwareArea.NgShuttle;
}
