using IBTM.Device;

namespace IBTM.BoltFeeder;

public sealed class BoltFeederHardwareSettings : IoHardwareSettings
{
    public BoltFeederHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.ShootingFeederBoltDetected] = 48,
            [InputIo.PickupFeederBoltDetected] = 52,
        };
        Outputs = new()
        {
            [OutputIo.ShootingFeederRunSignal] = CreateOutput(45),
        };
    }

    public override HardwareArea Area => HardwareArea.BoltFeeder;
}
