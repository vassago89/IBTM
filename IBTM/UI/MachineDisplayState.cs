using System.ComponentModel;

namespace IBTM.UI;

public enum MachineDisplayState
{
    [Description("Home Required")]
    HomeRequired,

    [Description("Servo Off")]
    ServoOff,

    [Description("Motion Fault")]
    MotionFault,

    [Description("Safety Stop")]
    SafetyStop,

    [Description("Ready")]
    Ready,

    [Description("Running")]
    Running,

    [Description("Homing")]
    Homing,

    [Description("Alarm")]
    Alarm,
}

public enum StartBlockReason
{
    [Description("")]
    None,

    [Description("Clear the cause, then press RESET")]
    Alarm,

    [Description("Clear PCB buffer, then press RESET")]
    BufferConflict,

    [Description("Clear the motion fault, then press RESET")]
    MotionFault,

    [Description("Release E-stop, then press RESET")]
    EmergencyStop,

    [Description("Restore air pressure, then press RESET")]
    AirPressure,

    [Description("Press RESET to restore servo power")]
    ServoOff,

    [Description("Run HOME ALL")]
    HomeRequired,

    [Description("Close doors, then press RESET")]
    DoorOpen,

    [Description("Select AUTO mode")]
    AutoMode,

    [Description("Enable a unit in Settings")]
    NoUnitEnabled,

    [Description("Complete bolt teaching")]
    TeachingIncomplete,
}

public enum HandlerDisplayState
{
    [Description("Disabled")]
    Disabled,

    [Description("Waiting for PCB Carrier")]
    WaitingForCarrier,

    [Description("PCB Carrier Available")]
    CarrierAvailable,

    [Description("PCB Detected")]
    PcbDetected,

    [Description("PCB at Buffer")]
    BufferPcbAvailable,

    [Description("Waiting for Buffer PCB")]
    WaitingForBufferPcb,

    [Description("Moving")]
    Moving,

    [Description("I/O Alarm")]
    IoAlarm,
}

public enum StationDisplayState
{
    [Description("Disabled")]
    Disabled,

    [Description("I/O Alarm")]
    IoAlarm,

    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Empty Carrier")]
    EmptyCarrier,

    [Description("Heat Sink Detected")]
    HeatSinkDetected,

    [Description("Working")]
    Working,

    [Description("Carrier OK")]
    CarrierOk,

    [Description("Carrier NG")]
    CarrierNg,
}
