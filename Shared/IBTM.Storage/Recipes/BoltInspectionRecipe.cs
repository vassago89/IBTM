using System;
using IBTM.Core;

namespace IBTM.Inspection;

public sealed class BoltInspectionRecipe
{
    public BoltInspectionRecipe()
    {
        DataMatrix1 = new();
        DataMatrix2 = new();
        HeightReference1 = new();
        HeightReference2 = new();
        Fiducials1 = new();
        Fiducials2 = new();
    }

    public FiducialRecipe Fiducials1 { get; set; }
    public FiducialRecipe Fiducials2 { get; set; }

    public int HeightMeasurementCount
    {
        get;
        set
        {
            if (value is < 1 or > 99)
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Enter 1 to 99 readings per point"));
            field = value;
        }
    } = 10;

    public FiducialRecipe GetFiducials(HeatSinkSlot pcb)
    {
        return pcb switch
        {
            HeatSinkSlot.HeatSink1 => Fiducials1,
            HeatSinkSlot.HeatSink2 => Fiducials2,
            _ => throw new ArgumentOutOfRangeException(nameof(pcb)),
        };
    }

    public DataMatrixInspectionRecipe DataMatrix1 { get; set; }
    public DataMatrixInspectionRecipe DataMatrix2 { get; set; }
    public HeightReferenceRecipe HeightReference1 { get; set; }
    public HeightReferenceRecipe HeightReference2 { get; set; }

    public HeightReferenceRecipe GetHeightReference(HeatSinkSlot pcb)
    {
        return pcb switch
        {
            HeatSinkSlot.HeatSink1 => HeightReference1,
            HeatSinkSlot.HeatSink2 => HeightReference2,
            _ => throw new ArgumentOutOfRangeException(nameof(pcb)),
        };
    }

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
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Use 0 to 255."));
            field = value;
        }
    } = 255;

}

public sealed class HeightReferenceRecipe
{
    // Camera-centred XY; the machine's camera-to-probe offset applies at movement time.
    public AxisPosition? Point1 { get; set; }
    public AxisPosition? Point2 { get; set; }
    public AxisPosition? Point3 { get; set; }

    public void Validate()
    {
        if (Point1 is not { } p1 || Point2 is not { } p2 || Point3 is not { } p3)
            throw new InvalidOperationException(UiText.Get("Teach all three height reference points"));
        HeightReferencePlane.GetTriangleDeterminant(p2.X - p1.X, p2.Y - p1.Y, p3.X - p1.X, p3.Y - p1.Y);
    }
}
