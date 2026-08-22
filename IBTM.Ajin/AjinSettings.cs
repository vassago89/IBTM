using IBTM.Core;

namespace IBTM.Ajin;

public sealed class AjinSettings : Setting
{
    public int InterruptNumber { get; set; } = 7;
    public string MotionParameterFile { get; set; } = "Settings/Default.mot";
    public double AccelerationMultiplier { get; set; } = 2.0;
}
