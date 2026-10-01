using System.ComponentModel;

namespace IBTM.PcbSupply;

public enum PcbSupplyRotationState
{
    [Description("Unrotated")]
    Unrotated,

    [Description("Between")]
    Between,

    [Description("Rotated")]
    Rotated,
}
