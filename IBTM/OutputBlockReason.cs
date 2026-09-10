using System.ComponentModel;

namespace IBTM;
// Output admission and automatic conveyor-clearance reasons.
// These do not replace machine alarms or clear their latched state.
public enum OutputBlockReason
{
    [Description("")]
    None = 0,

    [Description("Read only: machine status is unavailable.")]
    StateUnavailable,
    [Description("Read only: the machine is shutting down.")]
    ShuttingDown,
    [Description("Read only: control I/O is unavailable.")]
    IoUnavailable,
    [Description("Read only: switch the selector to MANUAL.")]
    AutoMode,
    [Description("Read only: release both emergency stops.")]
    EmergencyStop,

    [Description("Raise the Placement handler cylinders to clear the conveyor path.")]
    PlacementNotRaised,
    [Description("Move the Placement handler Z to its conveyor-clearance position.")]
    PlacementNotAtSafeZ,
    [Description("Raise both fastening heads to clear the conveyor path.")]
    FasteningNotRaised,
    [Description("Move the Fastening Z axis to its conveyor-clearance position.")]
    FasteningNotAtSafeZ,
    [Description("Raise the NG pickup: UP must be ON and DOWN must be OFF.")]
    NgPickupNotRaised,
    [Description("Clear the carrier detected at the NG pickup.")]
    NgCarrierDetected,
}
