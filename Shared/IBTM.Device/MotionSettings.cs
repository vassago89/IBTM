namespace IBTM.Device;

public sealed class MotionSettings
{
    public double HorizontalSpeed { get; set; } = 100.0;
    public double ZSpeed { get; set; } = 50.0;
    public HomeSettings HorizontalHome { get; set; } = new();
    public HomeSettings ZHome { get; set; } = new()
    {
        SearchSpeed = 10,
    };

    public HomeSettings Home(MotionAxis axis)
    {
        return axis == MotionAxis.Z ? ZHome : HorizontalHome;
    }
}
