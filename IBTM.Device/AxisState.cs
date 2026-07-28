namespace IBTM.Device;

public enum MotionAxis
{
    X,
    Y,
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
    bool NegativeLimit);
