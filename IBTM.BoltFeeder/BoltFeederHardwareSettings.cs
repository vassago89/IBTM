using IBTM.Device;

namespace IBTM.BoltFeeder;

public sealed class BoltFeederHardwareSettings : IoHardwareSettings
{
    public override HardwareArea Area => HardwareArea.BoltFeeder;

    public BoltFeederHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.LinearFeederBoltDetected] = 48,
            [InputIo.PickupFeederBoltDetected] = 52,
        };
        Outputs = new()
        {
            [OutputIo.LinearFeederRunSignal] = Output(45),
        };
    }
}
