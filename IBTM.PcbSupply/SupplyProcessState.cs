using System.ComponentModel;

namespace IBTM.PcbSupply;

public enum PcbCarrierSlot
{
    [Description("PCB 1")]
    Pcb1,

    [Description("PCB 2")]
    Pcb2,
}

public enum PcbCarrierState
{
    [Description("Waiting for PCB Carrier")]
    WaitingForCarrier,

    [Description("Processing PCB Carrier")]
    ProcessingCarrier,

    [Description("Releasing PCB Carrier")]
    ReleasingCarrier,
}
