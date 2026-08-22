using IBTM.Core;

namespace IBTM;

public sealed class ProcessSettings : Setting
{
    public bool PcbSupply { get; set; } = true;
    public bool PcbPlacement { get; set; } = true;
    public bool BoltFastening { get; set; } = true;
    public bool Inspection { get; set; } = true;
}
