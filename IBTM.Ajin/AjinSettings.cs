using IBTM.Core;

namespace IBTM.Ajin;

public sealed class AjinSettings : Setting
{
    public int InterruptNumber { get; set; } = 7;
    public string MotionParameterFile { get; set; } = "Settings/Default.mot";
    public double AccelerationMultiplier { get; set; } = 2.0;
    public int InputModuleOffset { get; set; }
    public int OutputModuleOffset { get; set; } = 2;
    public int IoChannelCount { get; set; } = 64;
}
