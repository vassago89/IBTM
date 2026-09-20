using System.Text.Json.Serialization;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandlerSettings : Setting
{
    public PcbPlacementHandlerSettings()
    {
        Motion = new();
        HandoffPosition = new();
    }

    public MotionSettings Motion { get; set; }
    [JsonPropertyName("BufferHandoffPosition")]
    public AxisPosition HandoffPosition { get; set; }
    public double? ReceiveZ { get; set; }

    public TeachingPosition[] GetTeachingPositions(PcbPlacementRecipe recipe)
    {
        return [
            new(
                TeachingTarget.PlacementReceiveZ,
                MotionGroup.PcbPlacementHandler,
                TeachMode.ZOnly,
                () => new() { Z = ReceiveZ ?? 0 },
                p => ReceiveZ = p.Z,
                this,
                isDefined: () => ReceiveZ is not null),
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

    public TeachingPosition GetHandoffTeachingPosition()
    {
        return new(
            TeachingTarget.PlacementHandoff,
            MotionGroup.PcbPlacementHandler,
            TeachMode.Full,
            () => HandoffPosition,
            p => (
                HandoffPosition.X,
                HandoffPosition.Y,
                HandoffPosition.Z) = (
                    p.X,
                    p.Y,
                    p.Z),
            this)
        { Staged = true };
    }
}
