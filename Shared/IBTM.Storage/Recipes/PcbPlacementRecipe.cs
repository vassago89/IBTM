using IBTM.Core;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementRecipe
{
    public PcbPlacementRecipe()
    {
        HeatSink1PcbPlacementPosition = new();
        HeatSink2PcbPlacementPosition = new();
    }

    public AxisPosition HeatSink1PcbPlacementPosition { get; set; }
    public AxisPosition HeatSink2PcbPlacementPosition { get; set; }
}
