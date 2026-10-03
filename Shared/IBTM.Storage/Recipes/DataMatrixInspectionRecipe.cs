using System;
using IBTM.Core;

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

    public int ThresholdMinimum
    {
        get;
        set
        {
            if (value is < 0 or > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Use a threshold from 0 to 255.");
            field = value;
        }
    }

    public int ThresholdMaximum
    {
        get;
        set
        {
            if (value is < 0 or > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Use a threshold from 0 to 255.");
            field = value;
        }
    } = 255;

    public int ThresholdStep
    {
        get;
        set
        {
            if (value is < 1 or > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Use a threshold step from 1 to 255.");
            field = value;
        }
    } = 5;

    // Zero disables dilation; otherwise retry each failed threshold after expanding black dots by this many pixels.
    public int DilationRadius
    {
        get;
        set
        {
            if (value is < 0 or > 5)
                throw new ArgumentOutOfRangeException(nameof(value), "Use a dilation radius from 0 to 5 pixels.");
            field = value;
        }
    } = 1;

    public void Validate()
    {
        if (ThresholdMinimum > ThresholdMaximum)
            throw new InvalidOperationException(UiText.Get("Data Matrix threshold minimum must not exceed maximum."));
    }
}
