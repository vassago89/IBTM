using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgShuttleHardwareSettings : IoHardwareSettings
{
    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.NgShuttle;
        }
    }

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
            [OutputIo.NgShuttleDown] = Output(68, 69, InputIo.NgShuttleDown, InputIo.NgShuttleUp),
        };
    }
}
