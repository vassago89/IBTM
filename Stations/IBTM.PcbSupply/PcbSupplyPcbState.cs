using System.ComponentModel;

namespace IBTM.PcbSupply;

public enum PcbSupplyPcbState
{
    [Description("No PCB")]
    None,

    [Description("PCB Detected")]
    Detected,

    [Description("PCB Secured")]
    Secured,
}
