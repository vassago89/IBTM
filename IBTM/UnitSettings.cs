using IBTM.Core;

namespace IBTM;

public sealed class UnitSettings : Setting
{
    public bool MainConveyor { get; set; } = true;
    public bool PcbSupply { get; set; } = true;
    public bool PcbPlacement { get; set; } = true;
    public bool PickupBoltFeeder { get; set; } = true;
    public bool ShootingBoltFeeder { get; set; } = true;
    public bool BoltFastening { get; set; } = true;
    public bool Inspection { get; set; } = true;
    public bool NgCarrierTransfer { get; set; } = true;
    public bool NgShuttle { get; set; } = true;
    public bool NgConveyor { get; set; } = true;

    internal bool IsMotionEnabled(MotionGroup group) => group switch
    {
        MotionGroup.PcbSupply => PcbSupply,
        MotionGroup.PcbPlacementHandler => PcbPlacement,
        MotionGroup.BoltFastening => BoltFastening,
        // Inspection and NG transfer use the same physical XY gantry.
        MotionGroup.InspectionGantry => Inspection || NgCarrierTransfer,
        _ => false,
    };

    internal bool HasEnabledUnit() =>
        MainConveyor
        || PcbSupply
        || PcbPlacement
        || PickupBoltFeeder
        || ShootingBoltFeeder
        || BoltFastening
        || Inspection
        || NgCarrierTransfer
        || NgShuttle
        || NgConveyor;
}
