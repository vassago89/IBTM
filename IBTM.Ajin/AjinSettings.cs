namespace IBTM.Ajin;

public sealed class AjinSettings
{
    public int InterruptNumber { get; set; } = 7;
    public string MotionParameterFile { get; set; } = "Settings/Default.mot";
    public double UnitsPerMillimeter { get; set; } = 1_000.0;
    public double AccelerationMultiplier { get; set; } = 2.0;
    public int InputModuleOffset { get; set; }
    public int OutputModuleOffset { get; set; } = 2;
    public int IoChannelCount { get; set; } = 64;
}
