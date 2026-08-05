using System.ComponentModel;

namespace IBTM.Hantas;

public enum AdcRegisterAccess
{
    [Description("Read Holding Registers")]
    ReadHoldingRegisters,

    [Description("Read Input Registers")]
    ReadInputRegisters,

    [Description("Write Single Register")]
    WriteSingleRegister,
}

public enum AdcRemoteRegister : ushort
{
    AlarmReset = 4000,
    DriverLock = 4001,
    RemoteStart = 4003,
    Preset = 4004,
    Direction = 4005,
}

public enum AdcResultRegister : ushort
{
    EventCount = 3200,
    FasteningTime = 3201,
    Preset = 3202,
    TargetTorque = 3203,
    ConvertedTorque = 3204,
    TargetSpeed = 3205,
    Angle1 = 3206,
    Angle2 = 3207,
    Angle3 = 3208,
    ScrewCount = 3209,
    Error = 3210,
    Direction = 3211,
    Status = 3212,
    SnugAngle = 3213,
}

public enum AdcDirection : ushort
{
    Fastening,
    Loosening,
}

public enum AdcEventStatus : ushort
{
    None,
    FasteningOk,
    FasteningNg,
    DirectionChanged,
    PresetChanged,
    AlarmReset,
    Error,
}

public enum AdcExceptionCode : byte
{
    IllegalFunction = 0x01,
    IllegalAddress = 0x02,
    InvalidDataLength = 0x03,
    InvalidCrc = 0x07,
    ByteCountExceeded = 0x0C,
    ValueOutOfRange = 0x0E,
}

public enum AdcFrameDirection
{
    Transmit,
    Receive,
}

public sealed record AdcFasteningResult(
    ushort EventCount,
    ushort FasteningTimeMilliseconds,
    ushort Preset,
    double TargetTorque,
    double Torque,
    ushort TargetSpeedRpm,
    double Angle1,
    double Angle2,
    double Angle3,
    ushort ScrewCount,
    ushort Error,
    AdcDirection Direction,
    AdcEventStatus Status,
    ushort SnugAngle);
