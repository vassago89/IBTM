namespace IBTM.Stations.Inspection;

public sealed class InspectionRecipe
{
    public AxisPos InspectPosition { get; set; } = new() { X = 100.0, Y = 95.0, Z = 10.0 };
    public AxisPos NgPickupPosition { get; set; } = new() { X = 100.0, Y = 95.0, Z = 20.0 };
    public AxisPos NgPlacePosition { get; set; } = new() { X = 100.0, Y = 25.0, Z = 30.0 };
}
