using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplySettings : Setting
{
    public PcbSupplySettings()
    {
        Motion = new();
        BufferHandoffPosition = new();
    }

    public MotionSettings Motion { get; set; }
    public double RotationZ { get; set; }
    public double CarrierY { get; set; }
    public AxisPosition BufferHandoffPosition { get; set; }

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
            Pick(TeachingTarget.SupplyPcb1Pick, recipe.Pcb1PickPosition),
            Pick(TeachingTarget.SupplyPcb2Pick, recipe.Pcb2PickPosition),
            new(
                TeachingTarget.SupplyBufferHandoff,
                MotionGroup.PcbSupply,
                TeachMode.Full,
                () => BufferHandoffPosition,
                p => (BufferHandoffPosition.X, BufferHandoffPosition.Y, BufferHandoffPosition.Z) = (p.X, p.Y, p.Z),
                this) { Staged = true },
        ];
    }

    private TeachingPosition Pick(TeachingTarget target, PcbPickPosition pick)
    {
        return new(
            target,
            MotionGroup.PcbSupply,
            TeachMode.XZOnly,
            () => new() { X = pick.X, Y = CarrierY, Z = pick.Z },
            p =>
            {
                pick.X = p.X;
                pick.Z = p.Z;
            });
    }
}
