using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplySettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public double RotationZ { get; set; }
    public double CarrierY { get; set; }
    // Handoff uses RotationZ; older saved handoff Z values are no longer read.
    public XyPosition BufferHandoffPosition { get; set; } = new();

    public TeachingPosition[] GetTeachingPositions(PcbSupplyRecipe recipe)
    {
        return [
            new(
                TeachingTarget.SafeZ,
                MotionGroup.PcbSupply,
                TeachMode.ZOnly,
                () => new() { Z = RotationZ },
                p => RotationZ = p.Z,
                this),
            new(
                TeachingTarget.SupplyCarrierY,
                MotionGroup.PcbSupply,
                TeachMode.YOnly,
                () => new() { Y = CarrierY },
                p => CarrierY = p.Y,
                this),
            .. recipe.GetTeachingPositions(this),
            new(
                TeachingTarget.SupplyBufferHandoff,
                MotionGroup.PcbSupply,
                TeachMode.XYOnly,
                () => new() { X = BufferHandoffPosition.X, Y = BufferHandoffPosition.Y },
                p => (BufferHandoffPosition.X, BufferHandoffPosition.Y) = (p.X, p.Y),
                this) { Staged = true },
        ];
    }
}
