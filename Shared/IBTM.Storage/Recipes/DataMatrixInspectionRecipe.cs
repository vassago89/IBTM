using System;

namespace IBTM.Inspection;

public sealed class DataMatrixInspectionRecipe
{
    public int? LightLevel
    {
        get;
        set
        {
            if (value is < 0 or > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 255, or leave blank for the recipe default.");
            field = value;
        }
    }

    public bool TryHarder { get; set; } = true;
    public bool TryInverted { get; set; } = true;
    public bool AutoRotate { get; set; }
    public bool PureBarcode { get; set; }

    // Null uses ZXing's automatic binarization, preserving existing recipes.
    public int? BinaryThreshold
    {
        get;
        set
        {
            if (value is < 0 or > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 255, or leave blank for automatic thresholding.");
            field = value;
        }
    }
}
