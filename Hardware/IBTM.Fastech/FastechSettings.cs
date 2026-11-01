using IBTM.Core;

namespace IBTM.Fastech;

public sealed class FastechSettings : Setting
{
    public FastechSettings()
    {
        BackupAxis = new();
    }

    public string PortName { get; set; } = string.Empty;
    public uint BaudRate { get; set; } = 115200;
    public FastechZAxisSettings BackupAxis { get; set; }
}

public sealed class FastechZAxisSettings
{
    public byte SlaveNumber { get; set; }
    public double PulsesPerMillimeter { get; set; }
}
