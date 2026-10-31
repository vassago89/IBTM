using IBTM.Core;

namespace IBTM.Fastech;

public sealed class FastechSettings : Setting
{
    public string PortName { get; set; } = string.Empty;
    public uint BaudRate { get; set; } = 115200;
}

public sealed class FastechZAxisSettings
{
    public byte SlaveNumber { get; set; }
    public double PulsesPerMillimeter { get; set; }
}
