using IBTM.Core;

namespace IBTM.Stations.PcbPlacement;

public sealed class PcbPlacementRecipe
{
    public AxisPos Fiducial1Position { get; set; } = new() { X = 8.0, Y = 18.0, Z = 10.0 };
    public AxisPos Fiducial2Position { get; set; } = new() { X = 12.0, Y = 18.0, Z = 10.0 };
    public AxisPos Pcb1PlacementPosition { get; set; } = new() { X = 7.0, Y = 32.0, Z = 16.0 };
    public AxisPos Pcb2PlacementPosition { get; set; } = new() { X = 13.0, Y = 32.0, Z = 16.0 };
}
