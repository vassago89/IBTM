using IBTM.Core;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementRecipe
{
    public AxisPos HeatSink1PcbPlacementPosition { get; set; } = new();
    public AxisPos HeatSink2PcbPlacementPosition { get; set; } = new();
}
