using IBTM.Core;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementRecipe
{
    public AxisPos Housing1PcbPlacementPosition { get; set; } = new();
    public AxisPos Housing2PcbPlacementPosition { get; set; } = new();
}
