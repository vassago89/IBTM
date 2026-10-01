using System.ComponentModel;

namespace IBTM.PcbSupply;

public enum PcbSupplyCylinderState
{
    [Description("Backward")]
    Backward,

    [Description("Between")]
    Between,

    [Description("Forward")]
    Forward,
}
