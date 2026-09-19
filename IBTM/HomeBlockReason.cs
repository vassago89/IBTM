using System.ComponentModel;

namespace IBTM;

public enum HomeBlockReason
{
    [Description("")]
    None,
    [Description("Restore I/O communication before HOME")]
    IoUnavailable,
    [Description("Placement is holding a PCB with the IPM lift down; release it before HOME")]
    PlacementHoldingPcb,
    [Description("Raise the Placement handler and IPM lift before HOME")]
    PlacementNotRaised,
    [Description("Raise both fastening heads before HOME")]
    FasteningNotRaised,
    [Description("Raise the NG pickup before HOME")]
    NgPickupNotRaised,
    [Description("Close the doors before HOME")]
    DoorOpen,
    [Description("Enable this unit in Settings before HOME")]
    UnitDisabled,
}
