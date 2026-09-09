using IBTM.Core;

namespace IBTM.Ajin;

public sealed class AjinSettings : Setting
{
    public int InterruptNumber { get; set; } = 7;
    public string MotionParameterFile { get; set; } = "Settings/Default.mot";
    public int[] RtexInputModules { get; set; } = [0, 1, 4];
    public int[] RtexOutputModules { get; set; } = [2, 3, 4];
}
