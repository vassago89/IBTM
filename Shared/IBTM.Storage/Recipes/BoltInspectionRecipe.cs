using System;

namespace IBTM.Inspection;

public sealed class BoltInspectionRecipe
{
    public int LightLevel
    {
        get;
        set
        {
            if (value < 0 || value > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 255.");
            field = value;
        }
    } = 255;

    public int BrightnessThreshold
    {
        get;
        set
        {
            if (value < 0 || value > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 255.");
            field = value;
        }
    } = 128;

    public double MinimumBrightRatio
    {
        get;
        set
        {
            if (!(value >= 0 && value <= 1))
                throw new ArgumentOutOfRangeException(nameof(value), "Use a ratio from 0 to 1.");
            field = value;
        }
    } = 0.01;
}
