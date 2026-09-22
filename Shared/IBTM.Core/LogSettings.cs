using System;
using System.IO;

namespace IBTM.Core;

public sealed class LogSettings : Setting
{
    public LogSettings()
    {
        Directory = Path.Combine(AppContext.BaseDirectory, "Logs");
        RetentionDays = 30;
    }

    public string Directory { get; set; }

    public int RetentionDays
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    }
}
