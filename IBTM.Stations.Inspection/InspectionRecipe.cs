using IBTM.Core.Geometry;

namespace IBTM.Stations.Inspection;

public sealed class InspectionRecipe
{
    public AxisPos Pcb1InspectionPosition { get; set; } = new() { X = 75.0, Y = 95.0, Z = 10.0 };
    public AxisPos Pcb2InspectionPosition { get; set; } = new() { X = 130.0, Y = 95.0, Z = 10.0 };
    public AxisPos NgCarrierPickupPosition { get; set; } = new() { X = 100.0, Y = 95.0, Z = 20.0 };
    public AxisPos NgStackPosition { get; set; } = new() { X = 100.0, Y = 25.0, Z = 30.0 };
}
