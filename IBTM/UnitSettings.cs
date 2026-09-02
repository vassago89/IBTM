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
    public bool NgConveyor { get; set; } = true;

    public bool HasEnabledUnit() =>
        MainConveyor
        || PcbSupply
        || PcbPlacement
        || PickupBoltFeeder
        || ShootingBoltFeeder
        || BoltFastening
        || Inspection
        || NgConveyor;

    public UnitSettings Snapshot() => new()
    {
        MainConveyor = MainConveyor,
        PcbSupply = PcbSupply,
        PcbPlacement = PcbPlacement,
        PickupBoltFeeder = PickupBoltFeeder,
        ShootingBoltFeeder = ShootingBoltFeeder,
        BoltFastening = BoltFastening,
        Inspection = Inspection,
        NgConveyor = NgConveyor,
    };
}
