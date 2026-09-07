using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyRecipe
{
    public PcbPickPosition Pcb1PickPosition { get; set; } = new();
    public PcbPickPosition Pcb2PickPosition { get; set; } = new();

    public TeachingPosition[] GetTeachingPositions(PcbSupplySettings settings) =>
    [
        Pick(TeachingTarget.SupplyPcb1Pick, Pcb1PickPosition, settings),
        Pick(TeachingTarget.SupplyPcb2Pick, Pcb2PickPosition, settings),
    ];

    private static TeachingPosition Pick(
        TeachingTarget target, PcbPickPosition pick, PcbSupplySettings settings) =>
        new(target, MotionGroup.PcbSupply, TeachMode.XZOnly,
            () => new() { X = pick.X, Y = settings.CarrierY, Z = pick.Z },
            p => { pick.X = p.X; pick.Z = p.Z; });
}

public sealed class PcbPickPosition
{
    public double X { get; set; }
    public double Z { get; set; }
}
