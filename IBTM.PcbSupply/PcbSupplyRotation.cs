using System.ComponentModel;

namespace IBTM.PcbSupply;

public enum PcbSupplyRotation
{
    [Description("Unrotated")]
    Unrotated,

    [Description("Between")]
    Between,

    [Description("Rotated")]
    Rotated,
}
