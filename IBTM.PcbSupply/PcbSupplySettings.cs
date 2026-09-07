using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplySettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public double RotationZ { get; set; }
    public double CarrierY { get; set; }
    public AxisPosition BufferHandoffPosition { get; set; } = new();
    public double BufferClearZ { get; set; }

    public TeachingPosition[] GetTeachingPositions(PcbSupplyRecipe recipe) =>
    [
        new(TeachingTarget.SafeZ, MotionGroup.PcbSupply, TeachMode.ZOnly,
            () => new() { Z = RotationZ }, p => RotationZ = p.Z, this),
        new(TeachingTarget.SupplyCarrierY, MotionGroup.PcbSupply, TeachMode.YOnly,
            () => new() { Y = CarrierY }, p => CarrierY = p.Y, this),
        .. recipe.GetTeachingPositions(this),
        new(TeachingTarget.SupplyBufferHandoff, MotionGroup.PcbSupply, TeachMode.Full,
            () => BufferHandoffPosition,
            p => (BufferHandoffPosition.X, BufferHandoffPosition.Y, BufferHandoffPosition.Z) = (p.X, p.Y, p.Z),
            this) { Staged = true },
        new(TeachingTarget.SupplyBufferClearZ, MotionGroup.PcbSupply, TeachMode.ZOnly,
            () => new() { Z = BufferClearZ }, p => BufferClearZ = p.Z, this) { Staged = true },
    ];
}
