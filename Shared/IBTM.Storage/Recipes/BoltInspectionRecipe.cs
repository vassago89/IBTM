using System;
using IBTM.Core;

namespace IBTM.Inspection;

public sealed class BoltInspectionRecipe
{
    public BoltInspectionRecipe()
    {
        DataMatrix1 = new();
        DataMatrix2 = new();
    }

    public DataMatrixInspectionRecipe DataMatrix1 { get; set; }
    public DataMatrixInspectionRecipe DataMatrix2 { get; set; }

    public DataMatrixInspectionRecipe GetDataMatrix(HeatSinkSlot pcb)
    {
        return pcb switch
        {
            HeatSinkSlot.HeatSink1 => DataMatrix1,
            HeatSinkSlot.HeatSink2 => DataMatrix2,
            _ => throw new ArgumentOutOfRangeException(nameof(pcb)),
        };
    }

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
