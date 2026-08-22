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
    [Description("Alarm Reset")]
    AlarmReset = 4000,

    [Description("Remote Start")]
    RemoteStart = 4003,

    [Description("Preset")]
    Preset = 4004,

    [Description("Direction")]
    Direction = 4005,
}

public enum AdcResultRegister : ushort
{
    [Description("Event Count")]
    EventCount = 3200,
    [Description("Fastening Time")]
    FasteningTime = 3201,
    [Description("Preset")]
    Preset = 3202,
    [Description("Target Torque")]
    TargetTorque = 3203,
    [Description("Converted Torque")]
    ConvertedTorque = 3204,
    [Description("Target Speed")]
    TargetSpeed = 3205,
    [Description("Angle 1")]
    Angle1 = 3206,
    [Description("Angle 2")]
    Angle2 = 3207,
    [Description("Angle 3")]
    Angle3 = 3208,
    [Description("Screw Count")]
    ScrewCount = 3209,
    [Description("Error")]
    Error = 3210,
    [Description("Direction")]
    Direction = 3211,
    [Description("Status")]
    Status = 3212,
    [Description("Snug Angle")]
    SnugAngle = 3213,
}

public enum AdcDirection : ushort
{
    [Description("Fastening")]
    Fastening,
    [Description("Loosening")]
    Loosening,
}

public enum AdcEventStatus : ushort
{
    [Description("None")]
    None,
    [Description("Fastening OK")]
    FasteningOk,
    [Description("Fastening NG")]
    FasteningNg,
    [Description("Direction Changed")]
    DirectionChanged,
    [Description("Preset Changed")]
    PresetChanged,
    [Description("Alarm Reset")]
    AlarmReset,
    [Description("Error")]
    Error,
}

public enum AdcExceptionCode : byte
{
    [Description("Illegal Function")]
    IllegalFunction = 0x01,
    [Description("Illegal Address")]
    IllegalAddress = 0x02,
    [Description("Invalid Data Length")]
    InvalidDataLength = 0x03,
    [Description("Invalid CRC")]
    InvalidCrc = 0x07,
    [Description("Byte Count Exceeded")]
    ByteCountExceeded = 0x0C,
    [Description("Value Out of Range")]
    ValueOutOfRange = 0x0E,
}

public enum AdcFrameDirection
{
    [Description("Transmit")]
    Transmit,
    [Description("Receive")]
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
