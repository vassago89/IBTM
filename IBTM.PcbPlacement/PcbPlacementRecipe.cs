using IBTM.Core;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementRecipe
{
    public AxisPosition HeatSink1PcbPlacementPosition { get; set; } = new();
    public AxisPosition HeatSink2PcbPlacementPosition { get; set; } = new();
}
