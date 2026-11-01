using IBTM.Core;

namespace IBTM.Fastech;

public sealed class FastechSettings : Setting
{
    public FastechSettings()
    {
        BackupAxis = new();
    }

    public string IpAddress { get; set; } = string.Empty;
    public FastechZAxisSettings BackupAxis { get; set; }
}

public sealed class FastechZAxisSettings
{
    public double PulsesPerMillimeter { get; set; }
}
