using System;
using System.IO;
using IBTM.Core;

namespace IBTM.Storage;

public sealed class PcbHistorySettings : Setting
{
    public PcbHistorySettings()
    {
        Directory = Path.Combine(AppContext.BaseDirectory, "Data", "Results");
    }

    public string Directory { get; set; }
}
