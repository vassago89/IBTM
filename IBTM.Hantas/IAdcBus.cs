using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Hantas;

public interface IAdcBus
{
    bool IsOpen { get; }
    string PortName { get; }
    int BaudRate { get; }

    event Action<AdcFrameDirection, byte[]>? FrameTransferred;

    string[] GetPortNames();
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

    Task<AdcFasteningResult> ReadFasteningResultAsync(
        byte slaveAddress,
        CancellationToken cancellationToken = default);

    Task ResetAlarmAsync(
        byte slaveAddress,
        CancellationToken cancellationToken = default);

    Task SelectPresetAsync(
        byte slaveAddress,
        ushort preset,
        CancellationToken cancellationToken = default);

    Task SetDirectionAsync(
        byte slaveAddress,
        AdcDirection direction,
        CancellationToken cancellationToken = default);

    Task StartAsync(
        byte slaveAddress,
        CancellationToken cancellationToken = default);

    Task StopAsync(
        byte slaveAddress,
        CancellationToken cancellationToken = default);
}
