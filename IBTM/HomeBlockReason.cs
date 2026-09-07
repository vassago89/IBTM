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
    [Description("Raise the Placement handler before HOME")]
    PlacementNotRaised,
    [Description("Raise both fastening heads before HOME")]
    FasteningNotRaised,
    [Description("Raise the NG pickup before HOME")]
    NgPickupNotRaised,
}
