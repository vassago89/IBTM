using IBTM.Core;

namespace IBTM.Hantas;

public sealed class HantasSettings : Setting
{
    public string PortName { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 115_200;
    public byte ShootingSlaveAddress { get; set; } = 1;
    public byte PickupSlaveAddress { get; set; } = 2;
}
