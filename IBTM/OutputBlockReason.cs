using System.ComponentModel;

namespace IBTM;

// First blocking condition for manual outputs. None permits the next check;
// these reasons do not replace machine alarms or clear their latched state.
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
    [Description("Read only: restore normal air pressure.")]
    AirPressureLow,
    [Description("Read only: reset the safety, I/O or process alarm first.")]
    MachineAlarm,
    [Description("Read only: wait for the current operation to stop.")]
    Busy,
    [Description("The machine alarm changed during the output test.")]
    AlarmChanged,

    [Description("Use the dedicated Manual Control / Station Teaching operation for this output.")]
    DedicatedControlRequired,
    [Description("Enable the main conveyor before testing its outputs.")]
    MainConveyorDisabled,
    [Description("Enable the NG conveyor before testing its stopper.")]
    NgConveyorDisabled,
    [Description("Enable PCB Supply before testing its interface signal.")]
    PcbSupplyDisabled,
    [Description("Enable the associated handler before testing this output.")]
    HandlerDisabled,
    [Description("Initialize the associated handler's motion connection.")]
    HandlerUnavailable,
    [Description("Restore the servo main contactor feedback.")]
    ServoPowerOff,
    [Description("Home the associated handler before testing this output.")]
    HandlerNotHomed,
    [Description("Enable the associated handler's servos.")]
    HandlerServoOff,
    [Description("Clear the associated handler's motion fault.")]
    HandlerMotionFault,
    [Description("The other handler is inside the PCB buffer.")]
    OtherHandlerInBuffer,
    [Description("Output interlock: check the handler position and cylinder clearance.")]
    OutputInterlock,

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

    [Description("Remove carriers from the main conveyor before this motor-only test.")]
    MainConveyorCarrierDetected,
    [Description("Empty the NG conveyor and shuttle before testing its stopper.")]
    NgConveyorOccupied,
    [Description("Remove the carrier from this station before testing the stopper.")]
    StationCarrierDetected,
    [Description("Lower this station's backup plate: UP must be OFF and DOWN must be ON.")]
    BackupPlateNotDown,
    [Description("Clear the PCB buffer conflict before testing interface signals.")]
    BufferConflict,
    [Description("Empty the machine before testing interface signals.")]
    MaterialDetected,
    [Description("Stop the connected equipment: its available/ready inputs must be OFF for this test.")]
    PeerHandshakeActive,
}
