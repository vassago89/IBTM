using System.ComponentModel;

namespace IBTM;

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

    [Description("Select TEACHING (MANUAL) mode for REPEAT")]
    TeachingMode,

    [Description("Enable a unit in Settings")]
    NoUnitEnabled,

    [Description("Repeat requires Main Conveyor, NG Transfer, NG Shuttle and NG Conveyor")]
    RepeatRouteUnavailable,

    [Description("Complete bolt teaching")]
    TeachingIncomplete,
}
