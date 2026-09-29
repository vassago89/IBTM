using System.ComponentModel;

namespace IBTM;

public enum HomeBlockReason
{
    [Description("")]
    None,
    [Description("I/O communication unavailable")]
    IoUnavailable,
    [Description("Release held PCB before HOME (IPM lift down)")]
    PlacementHoldingPcb,
    [Description("Raise placement handler and IPM lift")]
    PlacementNotRaised,
    [Description("Raise both fastening heads")]
    FasteningNotRaised,
    [Description("Raise NG pickup")]
    NgPickupNotRaised,
    [Description("Close doors")]
    DoorOpen,
    [Description("Unit disabled in Settings")]
    UnitDisabled,
}
