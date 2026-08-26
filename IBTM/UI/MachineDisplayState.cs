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

    [Description("Ready")]
    Ready,

    [Description("Manual Mode")]
    ManualMode,

    [Description("Running")]
    Running,

    [Description("Homing")]
    Homing,

    [Description("Alarm")]
    Alarm,
}

public enum HandlerDisplayState
{
    [Description("Waiting for PCB Carrier")]
    WaitingForCarrier,

    [Description("PCB Carrier Available")]
    CarrierAvailable,

    [Description("PCB Detected")]
    PcbDetected,

    [Description("Waiting for PCB")]
    WaitingForPcb,

    [Description("Moving")]
    Moving,

    [Description("I/O Alarm")]
    IoAlarm,
}

public enum StationDisplayState
{
    [Description("I/O Alarm")]
    IoAlarm,

    [Description("No Housing")]
    NoHousing,

    [Description("Housing Detected")]
    HousingDetected,

    [Description("Working")]
    Working,

    [Description("Carrier OK")]
    CarrierOk,

    [Description("Carrier NG")]
    CarrierNg,
}
