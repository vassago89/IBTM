using System.ComponentModel;

namespace IBTM.BoltFastening;

public enum BoltFasteningState
{
    [Description("Waiting for Carrier Jig")]
    WaitingForCarrier,

    [Description("Waiting for Carrier Jig Seating")]
    WaitingForSeat,

    [Description("Ready to Fasten")]
    ReadyToFasten,

    [Description("Waiting for Transfer")]
    WaitingForTransfer,
}
