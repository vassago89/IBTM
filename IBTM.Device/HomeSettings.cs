namespace IBTM.Device;

public sealed class HomeSettings
{
    public double SearchSpeed { get; set; } = 15;
    public double DetectionSpeed { get; set; } = 3;
    public double ApproachSpeed { get; set; } = 1.5;
    public double FineSpeed { get; set; } = 0.15;
    public double SearchAccelerationSeconds { get; set; } = 1;
    public double DetectionAccelerationSeconds { get; set; } = 2;
}
