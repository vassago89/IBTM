using System.Text.Json.Serialization;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplySettings : Setting
{
    public PcbSupplySettings()
    {
        Motion = new();
        HandoffPosition = new();
    }

    public MotionSettings Motion { get; set; }
    public double RotationZ { get; set; }
    [JsonPropertyName("BufferHandoffPosition")]
    public AxisPosition HandoffPosition { get; set; }

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
            Pick(TeachingTarget.SupplyPcb1Pick, recipe.Pcb1PickPosition),
            Pick(TeachingTarget.SupplyPcb2Pick, recipe.Pcb2PickPosition),
            new(
                TeachingTarget.SupplyHandoff,
                MotionGroup.PcbSupply,
                TeachMode.Full,
                () => HandoffPosition,
                p => (HandoffPosition.X, HandoffPosition.Y, HandoffPosition.Z) = (p.X, p.Y, p.Z),
                this),
        ];
    }

    private TeachingPosition Pick(TeachingTarget target, PcbPickPosition pick)
    {
        return new(
            target,
            MotionGroup.PcbSupply,
            TeachMode.Full,
            () => new() { X = pick.X, Y = pick.Y ?? 0, Z = pick.Z },
            p =>
            {
                pick.X = p.X;
                pick.Y = p.Y;
                pick.Z = p.Z;
            },
            isDefined: () => pick.Y is not null);
    }
}
