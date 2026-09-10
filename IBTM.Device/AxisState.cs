using System.ComponentModel;

namespace IBTM.Device;

public enum MotionAxis
{
    [Description("X")]
    X,

    [Description("Y")]
    Y,

    [Description("Z")]
    Z,
}

public readonly record struct AxisState(
    bool Homed,
    bool ServoOn,
    bool Alarm,
    bool InPosition,
    bool Emergency,
    bool HomeSensor,
    bool PositiveLimit,
    bool NegativeLimit,
    bool InMotion = false);
