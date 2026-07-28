using IBTM.Core;

namespace IBTM.Stations.PcbPlacement;

public sealed class PcbPlacementRecipe
{
    public AxisPos Fiducial1Position { get; set; } = new() { X = 80.0, Y = 60.0, Z = 10.0 };
    public AxisPos Fiducial2Position { get; set; } = new() { X = 120.0, Y = 60.0, Z = 10.0 };
    public AxisPos Pcb1PlacePosition { get; set; } = new() { X = 75.0, Y = 95.0, Z = 20.0 };
    public AxisPos Pcb2PlacePosition { get; set; } = new() { X = 130.0, Y = 95.0, Z = 20.0 };
}
