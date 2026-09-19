namespace IBTM.Device;

public sealed class MotionSettings
{
    public MotionSettings()
    {
        HorizontalHome = new();
        ZHome = new()
        {
            SearchSpeed = 10,
            DetectionSpeed = 2,
            ApproachSpeed = 1,
            FineSpeed = 0.1,
        };
    }

    public double HorizontalSpeed { get; set; } = 100.0;
    public double ZSpeed { get; set; } = 50.0;
    public double AccelerationSeconds { get; set; } = 0.5;
    public double DecelerationSeconds { get; set; } = 0.5;
    public HomeSettings HorizontalHome { get; set; }
    public HomeSettings ZHome { get; set; }

    public HomeSettings Home(MotionAxis axis)
    {
        return axis == MotionAxis.Z ? ZHome : HorizontalHome;
    }
}
