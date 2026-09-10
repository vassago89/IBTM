using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandlerSettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public double BufferEntryZ { get; set; }
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

    public TeachingPosition[] GetTeachingPositions()
    {
        return [new(
            TeachingTarget.SafeZ,
            MotionGroup.PcbPlacementHandler,
            TeachMode.ZOnly,
            () => new() { Z = BufferEntryZ },
            p => BufferEntryZ = p.Z,
            this),];
    }
}
