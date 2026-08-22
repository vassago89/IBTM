using IBTM.Core;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementRecipe
{
    public AxisPos Fiducial1Position { get; set; } = new();
    public AxisPos Fiducial2Position { get; set; } = new();
    public AxisPos Housing1PcbPlacementPosition { get; set; } = new();
    public AxisPos Housing2PcbPlacementPosition { get; set; } = new();
}
