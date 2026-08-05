using System.ComponentModel;

namespace IBTM.UI;

public enum DisplayTone
{
    Neutral,
    Ready,
    Waiting,
    Active,
    Alarm,
}

public enum AxisDisplayState
{
    [Description("Ready")]
    Ready,

    [Description("Moving")]
    Moving,

    [Description("Home Required")]
    HomeRequired,

    [Description("Servo Off")]
    ServoOff,

    [Description("Positive Limit")]
    PositiveLimit,

    [Description("Negative Limit")]
    NegativeLimit,

    [Description("Emergency")]
    Emergency,

    [Description("Alarm")]
    Alarm,
}

public enum MachineDisplayState
{
    [Description("Home Required")]
    HomeRequired,

    [Description("Servo Off")]
    ServoOff,

    [Description("Motion Fault")]
    MotionFault,

    [Description("Ready")]
    Ready,

    [Description("Running")]
    Running,

    [Description("Homing")]
    Homing,

    [Description("Recovery Required")]
    RecoveryRequired,

    [Description("NG Capacity Reached")]
    NgCapacityReached,

    [Description("Alarm")]
    Alarm,
}

public enum HandlerDisplayState
{
    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Carrier Available")]
    CarrierAvailable,

    [Description("Waiting for PCB")]
    WaitingForPcb,

    [Description("PCB on Buffer")]
    PcbOnBuffer,

    [Description("Holding PCB")]
    HoldingPcb,

    [Description("Housing Detected")]
    HousingDetected,

    [Description("Moving")]
    Moving,

    [Description("Recovery Required")]
    RecoveryRequired,

    [Description("I/O Alarm")]
    IoAlarm,
}

public enum StationDisplayState
{
    [Description("Empty")]
    Empty,

    [Description("Carrier Jig Ready")]
    CarrierJigReady,

    [Description("Working")]
    Working,
}

public enum NgHandlingDisplayState
{
    [Description("Empty")]
    Empty,

    [Description("Carrier Jig Stored")]
    Occupied,

    [Description("Capacity Reached")]
    Full,
}

public enum OperatorMessage
{
    [Description("Home all axes to enable operation")]
    HomeAllAxes,

    [Description("Machine ready for operation")]
    ReadyForOperation,

    [Description("Homing all axes — Z axes first")]
    HomingAxes,

    [Description("Emergency stop is active")]
    EmergencyStop,

    [Description("Safety door is open")]
    DoorOpen,

    [Description("Air pressure is low")]
    AirPressureLow,

    [Description("Reset the motion alarm before continuing")]
    ResetMotionAlarm,

    [Description("Enable all motion servos before continuing")]
    EnableServos,

    [Description("Reset the machine alarm before continuing")]
    ResetMachineAlarm,

    [Description("Supply handler I/O feedback timed out")]
    SupplyIoTimeout,

    [Description("Placement handler I/O feedback timed out")]
    PlacementIoTimeout,

    [Description("Clear the NG carrier count before continuing")]
    ClearNgCarrier,

    [Description("Recover the handler from the PCB buffer")]
    RecoverBuffer,

    [Description("Carrier jig is moving on the main conveyor")]
    CarrierJigMoving,

    [Description("Upstream PCB carrier is available")]
    UpstreamCarrierAvailable,

    [Description("Supply handler is moving")]
    SupplyMoving,

    [Description("Rotate the supply handler before buffer handoff")]
    RotateSupplyHandler,

    [Description("PCB is ready for buffer handoff")]
    PcbReadyForBuffer,

    [Description("PCB is waiting on the buffer jig")]
    PcbOnBuffer,

    [Description("Placement handler is moving")]
    PlacementMoving,

    [Description("PCB is ready for alignment")]
    PcbReadyForAlignment,

    [Description("Bolt fastening is in progress")]
    BoltFastening,

    [Description("Inspection or NG transfer is in progress")]
    Inspection,
}
