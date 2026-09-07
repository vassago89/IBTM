using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementRecipe
{
    public AxisPosition HeatSink1PcbPlacementPosition { get; set; } = new();
    public AxisPosition HeatSink2PcbPlacementPosition { get; set; } = new();

    public TeachingPosition[] GetTeachingPositions() =>
    [
        new(TeachingTarget.HeatSink1PcbPlacement, MotionGroup.PcbPlacementHandler, TeachMode.Full,
            () => HeatSink1PcbPlacementPosition, p => HeatSink1PcbPlacementPosition = p),
        new(TeachingTarget.HeatSink2PcbPlacement, MotionGroup.PcbPlacementHandler, TeachMode.Full,
            () => HeatSink2PcbPlacementPosition, p => HeatSink2PcbPlacementPosition = p),
    ];
}
