using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandlerSettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public AxisPosition BufferHandoffPosition { get; set; } = new();

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
