using IBTM.Device;

namespace IBTM.BoltFeeder;

public sealed class BoltFeederHardwareSettings : IoHardwareSettings
{
    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.BoltFeeder;
        }
    }

    public BoltFeederHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.ShootingFeederBoltDetected] = 48,
            [InputIo.PickupFeederBoltDetected] = 52,
        };
        Outputs = new()
        {
            [OutputIo.ShootingFeederRunSignal] = Output(45),
        };
    }
}
