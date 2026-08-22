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

    [Description("PCB on Buffer")]
    PcbOnBuffer,

    [Description("PCB Detected")]
    PcbDetected,

    [Description("Housing Detected")]
    HousingDetected,

    [Description("No Housing")]
    NoHousing,

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
}
