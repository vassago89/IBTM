using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public interface IAdcBus
{
    bool IsOpen { get; }

    string PortName { get; }

    int BaudRate { get; }

    // Receive notifications are raw chunks, not necessarily complete or valid frames.
    event Action<AdcFrameDirection, byte[]>? FrameTransferred;

    string[] GetPortNames();
    // An existing connection must match both requested settings; otherwise Open must fail.
    void Open(string portName, int baudRate);
    void Close();

    Task<ushort[]> ReadHoldingRegistersAsync(
        byte slaveAddress,
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default);

    Task<ushort[]> ReadInputRegistersAsync(
        byte slaveAddress,
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default);

    Task WriteRegisterAsync(
        byte slaveAddress,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadDeviceInformationAsync(
        byte slaveAddress,
        CancellationToken cancellationToken = default);

    async Task<AdcFasteningResult> ReadFasteningResultAsync(
        byte slaveAddress,
        CancellationToken cancellationToken = default)
    {
        var values = await ReadInputRegistersAsync(
            slaveAddress,
            (ushort)AdcResultRegister.EventCount,
            AdcFasteningResult.RegisterCount,
            cancellationToken);
        return AdcFasteningResult.FromRegisters(values);
    }

    async Task<AdcControllerStatus> ReadControllerStatusAsync(
        byte slaveAddress,
        CancellationToken cancellationToken = default)
    {
        var values = await ReadInputRegistersAsync(
            slaveAddress,
            (ushort)AdcStatusRegister.Preset,
            AdcControllerStatus.RegisterCount,
            cancellationToken);
        return AdcControllerStatus.FromRegisters(values);
    }

    Task ResetAlarmAsync(byte slaveAddress, CancellationToken cancellationToken = default)
    {
        return WriteRegisterAsync(
            slaveAddress,
            (ushort)AdcRemoteRegister.AlarmReset,
            1,
            cancellationToken);
    }

    Task SelectPresetAsync(
        byte slaveAddress,
        ushort preset,
        CancellationToken cancellationToken = default)
    {
        return WriteRegisterAsync(
            slaveAddress,
            (ushort)AdcRemoteRegister.Preset,
            preset,
            cancellationToken);
    }

    Task SetDirectionAsync(
        byte slaveAddress,
        AdcDirection direction,
        CancellationToken cancellationToken = default)
    {
        return WriteRegisterAsync(
            slaveAddress,
            (ushort)AdcRemoteRegister.Direction,
            (ushort)direction,
            cancellationToken);
    }

    Task StartAsync(byte slaveAddress, CancellationToken cancellationToken = default)
    {
        return WriteRegisterAsync(
            slaveAddress,
            (ushort)AdcRemoteRegister.RemoteStart,
            1,
            cancellationToken);
    }

    Task StopAsync(byte slaveAddress, CancellationToken cancellationToken = default)
    {
        return WriteRegisterAsync(
            slaveAddress,
            (ushort)AdcRemoteRegister.RemoteStart,
            0,
            cancellationToken);
    }
}

public enum AdcFunctionCode : byte
{
    [Description("Read Holding Registers")]
    ReadHoldingRegisters = 0x03,

    [Description("Read Input Registers")]
    ReadInputRegisters = 0x04,

    [Description("Write Single Register")]
    WriteSingleRegister = 0x06,

    [Description("Request Device Information")]
    RequestDeviceInformation = 0x11,
}

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

public enum AdcStatusRegister : ushort
{
    [Description("Current Preset")]
    Preset = 3303,
    [Description("Ready")]
    Ready = 3306,
    [Description("Motor Run")]
    MotorRun = 3307,
    [Description("Current Alarm")]
    Alarm = 3308,
    [Description("Current Direction")]
    Direction = 3309,
}

public sealed record AdcControllerStatus(
    ushort Preset,
    bool Ready,
    bool Running,
    ushort Alarm,
    AdcDirection Direction)
{
    public const ushort RegisterCount = (ushort)AdcStatusRegister.Direction - (ushort)AdcStatusRegister.Preset + 1;

    internal static AdcControllerStatus FromRegisters(ushort[] values)
    {
        ushort Read(AdcStatusRegister register)
        {
            return values[(ushort)register - (ushort)AdcStatusRegister.Preset];
        }

        return new(
            Read(AdcStatusRegister.Preset),
            Read(AdcStatusRegister.Ready) != 0,
            Read(AdcStatusRegister.MotorRun) != 0,
            Read(AdcStatusRegister.Alarm),
            (AdcDirection)Read(AdcStatusRegister.Direction));
    }
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
    ushort SnugAngle)
{
    private const ushort FirstRegister = (ushort)AdcResultRegister.EventCount;
    public const ushort RegisterCount = (ushort)((ushort)AdcResultRegister.SnugAngle - FirstRegister + 1);
    private const double RegisterScale = 100.0;

    internal static AdcFasteningResult FromRegisters(ushort[] values)
    {
        ushort Read(AdcResultRegister register)
        {
            return values[(ushort)register - FirstRegister];
        }

        double ReadScaled(AdcResultRegister register)
        {
            return Read(register) / RegisterScale;
        }

        return new(
            Read(AdcResultRegister.EventCount),
            Read(AdcResultRegister.FasteningTime),
            Read(AdcResultRegister.Preset),
            ReadScaled(AdcResultRegister.TargetTorque),
            ReadScaled(AdcResultRegister.ConvertedTorque),
            Read(AdcResultRegister.TargetSpeed),
            ReadScaled(AdcResultRegister.Angle1),
            ReadScaled(AdcResultRegister.Angle2),
            ReadScaled(AdcResultRegister.Angle3),
            Read(AdcResultRegister.ScrewCount),
            Read(AdcResultRegister.Error),
            (AdcDirection)Read(AdcResultRegister.Direction),
            (AdcEventStatus)Read(AdcResultRegister.Status),
            Read(AdcResultRegister.SnugAngle));
    }
}
