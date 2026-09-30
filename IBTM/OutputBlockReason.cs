using System.ComponentModel;

namespace IBTM;
// Output admission and automatic conveyor-clearance reasons.
// These do not replace machine alarms or clear their latched state.
public enum OutputBlockReason
{
    [Description("")]
    None = 0,

    [Description("Shutting down")]
    ShuttingDown,
    [Description("Control I/O unavailable")]
    IoUnavailable,
    [Description("Switch to MANUAL")]
    AutoMode,
    [Description("Release both E-stops")]
    EmergencyStop,

    [Description("Raise placement cylinders")]
    PlacementNotRaised,
    [Description("Placement Z: conveyor clearance required")]
    PlacementNotAtSafeZ,
    [Description("Raise both fastening heads")]
    FasteningNotRaised,
    [Description("Fastening Z: conveyor clearance required")]
    FasteningNotAtSafeZ,
    [Description("Raise NG pickup (UP=ON, DOWN=OFF)")]
    NgPickupNotRaised,
    [Description("NG transfer pending")]
    NgTransferPending,

    [Description("Operation or stop cleanup in progress")]
    Busy,

    [Description("Move Supply to handoff XYZ before rotating.")]
    SupplyNotAtHandoff,
}
