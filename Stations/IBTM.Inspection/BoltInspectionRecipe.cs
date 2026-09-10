namespace IBTM.Inspection;

public sealed class BoltInspectionRecipe
{
    public double ExposureMicroseconds { get; set; } = 500.0;
    public double Gain { get; set; }
    public int LightLevel { get; set; } = 255;
    public int RegionSizePixels { get; set; } = 128;
    public double MinimumMaskRatio { get; set; } = 0.001;
}
