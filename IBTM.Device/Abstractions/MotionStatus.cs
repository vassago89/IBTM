namespace IBTM.Device.Abstractions;

public sealed record MotionStatus(
    bool IsOriginDone,
    bool IsServoOn,
    bool IsEmergency,
    bool IsAlarm,
    bool IsInPosition,
    bool IsHome,
    bool IsLimitPositive,
    bool IsLimitNegative);
