using System;

namespace IBTM.Inspection;

public sealed class BoltInspectionRecipe
{
    private double _exposureMicroseconds = 500.0;
    private double _gain;
    private int _lightLevel = 255;
    private int _brightnessThreshold = 128;
    private double _minimumBrightRatio = 0.01;

    public double ExposureMicroseconds
    {
        get
        {
            return _exposureMicroseconds;
        }
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Use a finite exposure greater than 0 microseconds.");
            _exposureMicroseconds = value;
        }
    }

    public double Gain
    {
        get
        {
            return _gain;
        }
        set
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), "Use a finite gain supported by the camera.");
            _gain = value;
        }
    }

    public int LightLevel
    {
        get
        {
            return _lightLevel;
        }
        set
        {
            if (value < 0 || value > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 255.");
            _lightLevel = value;
        }
    }

    public int BrightnessThreshold
    {
        get
        {
            return _brightnessThreshold;
        }
        set
        {
            if (value < 0 || value > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 255.");
            _brightnessThreshold = value;
        }
    }

    public double MinimumBrightRatio
    {
        get
        {
            return _minimumBrightRatio;
        }
        set
        {
            if (!(value >= 0 && value <= 1))
                throw new ArgumentOutOfRangeException(nameof(value), "Use a ratio from 0 to 1.");
            _minimumBrightRatio = value;
        }
    }
}
