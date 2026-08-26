using IBTM.Core;

namespace IBTM.Inspection;

public sealed class BoltInspectionSettings : Setting
{
    public string ModelFile { get; set; } = "Models/BoltRecess.dat";
    public float MaskThreshold { get; set; } = 0.5f;
    public double MinimumMaskRatio { get; set; } = 0.001;
}
