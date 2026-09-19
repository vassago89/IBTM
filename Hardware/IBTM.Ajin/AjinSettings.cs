using IBTM.Core;

namespace IBTM.Ajin;

public sealed class AjinSettings : Setting
{
    public AjinSettings()
    {
        RtexInputModules = [0, 1, 4];
        RtexOutputModules = [2, 3, 4];
    }

    public int InterruptNumber { get; set; } = 7;
    public int[] RtexInputModules { get; set; }
    public int[] RtexOutputModules { get; set; }
}
