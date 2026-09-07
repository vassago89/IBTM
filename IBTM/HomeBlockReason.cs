using System.ComponentModel;

namespace IBTM;

public enum HomeBlockReason
{
    [Description("")]
    None,
    [Description("Restore I/O communication before HOME")]
    IoUnavailable,
    [Description("Remove all carriers before HOME")]
    CarrierDetected,
    [Description("Raise the Placement handler and IPM cylinders before HOME")]
    PlacementNotRaised,
    [Description("Raise the bolt table and both heads before HOME")]
    FasteningNotRaised,
    [Description("Raise the NG pickup before HOME")]
    NgPickupNotRaised,
}
