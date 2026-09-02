using IBTM.Core;

namespace IBTM.Ajin;

public sealed class AjinSettings : Setting
{
    public int InterruptNumber { get; set; } = 7;
    public string MotionParameterFile { get; set; } = "Settings/Default.mot";
    public double AccelerationMultiplier { get; set; } = 2.0;
    public int[] RtexInputModules { get; set; } = [0, 1, 4];
    public int[] RtexOutputModules { get; set; } = [2, 3, 4];
    public double HomeSecondVelocityRatio { get; set; } = 0.2;
    public double HomeThirdVelocityRatio { get; set; } = 0.1;
    public double HomeLastVelocityRatio { get; set; } = 0.01;
    public double HomeSecondAccelerationRatio { get; set; } = 0.1;
}
