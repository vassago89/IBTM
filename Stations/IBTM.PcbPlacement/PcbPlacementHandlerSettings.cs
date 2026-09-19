using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandlerSettings : Setting
{
    public PcbPlacementHandlerSettings()
    {
        Motion = new();
        BufferHandoffPosition = new();
    }

    public MotionSettings Motion { get; set; }
    public AxisPosition BufferHandoffPosition { get; set; }

    public TeachingPosition[] GetTeachingPositions(PcbPlacementRecipe recipe)
    {
        return [
            new(
                TeachingTarget.HeatSink1PcbPlacement,
                MotionGroup.PcbPlacementHandler,
                TeachMode.Full,
                () => recipe.HeatSink1PcbPlacementPosition,
                p => recipe.HeatSink1PcbPlacementPosition = p),
            new(
                TeachingTarget.HeatSink2PcbPlacement,
                MotionGroup.PcbPlacementHandler,
                TeachMode.Full,
                () => recipe.HeatSink2PcbPlacementPosition,
                p => recipe.HeatSink2PcbPlacementPosition = p),
        ];
    }

    public TeachingPosition GetBufferTeachingPosition()
    {
        return new(
            TeachingTarget.PlacementBufferHandoff,
            MotionGroup.PcbPlacementHandler,
            TeachMode.Full,
            () => BufferHandoffPosition,
            p => (
                BufferHandoffPosition.X,
                BufferHandoffPosition.Y,
                BufferHandoffPosition.Z) = (
                    p.X,
                    p.Y,
                    p.Z),
            this)
        { Staged = true };
    }
}
