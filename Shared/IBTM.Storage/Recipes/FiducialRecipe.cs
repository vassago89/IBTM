using System;
using IBTM.Core;

namespace IBTM.Inspection;

public sealed class FiducialRecipe
{
    public FiducialRecipe()
    {
        First = new();
        Second = new();
    }

    public bool Enabled { get; set; }
    public FiducialMark First { get; set; }
    public FiducialMark Second { get; set; }
}

public sealed class FiducialMark
{
    public FiducialMark()
    {
        Contour = [];
    }

    public AxisPosition? CameraPosition { get; set; }
    public PixelRegion? Region { get; set; }
    public Point2D[] Contour { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public int? LightLevel { get; set; }
    public bool WhiteMark { get; set; }
    public int Threshold
    {
        get;
        set
        {
            if (value is < 0 or > 255)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = 127;
    public int SearchMarginPixels
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 50;
    public double MaximumShapeError
    {
        get;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = 0.01;
    public double SizeTolerancePercent
    {
        get;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value >= 100)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = 20;
}
