using IBTM.Core;

namespace IBTM;

public sealed class UnitSettings : Setting
{
    public bool MainConveyor { get; set; } = true;
    public bool PcbSupply { get; set; } = true;
    public bool PcbPlacement { get; set; } = true;
    public bool PickupBoltFeeder { get; set; } = true;
    public bool LinearBoltFeeder { get; set; } = true;
    public bool BoltFastening { get; set; } = true;
    public bool Inspection { get; set; }
    public bool NgConveyor { get; set; }
}
