using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualAdcBus : IAdcBus
{
    private const string VirtualPort = "Virtual";

    private readonly ConcurrentDictionary<byte, Controller> _controllers = [];
    public bool IsOpen { get; private set; }
    public string PortName => IsOpen ? VirtualPort : string.Empty;
    public int BaudRate { get; private set; }

    public event Action<AdcFrameDirection, byte[]>? FrameTransferred;

    public void SetNextFasteningResult(
        byte slaveAddress,
        AdcEventStatus status) =>
        GetController(slaveAddress).NextStatus = status;

    public string[] GetPortNames() => [VirtualPort];

    public void Open(string portName, int baudRate)
    {
        IsOpen = true;
        BaudRate = baudRate;
    }

    public void Close()
    {
        IsOpen = false;
        BaudRate = 0;
    }

    public Task<ushort[]> ReadHoldingRegistersAsync(
        byte slaveAddress,
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default) =>
        ReadRegistersAsync(
            slaveAddress,
            AdcFunctionCode.ReadHoldingRegisters,
            address,
            count,
            cancellationToken);

    public Task<ushort[]> ReadInputRegistersAsync(
        byte slaveAddress,
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default) =>
        ReadRegistersAsync(
            slaveAddress,
            AdcFunctionCode.ReadInputRegisters,
            address,
            count,
            cancellationToken);

    public Task WriteRegisterAsync(
        byte slaveAddress,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var controller = GetController(slaveAddress);
        controller.Registers[address] = value;

        switch ((AdcRemoteRegister)address)
        {
            case AdcRemoteRegister.AlarmReset:
                controller.Error = 0;
                controller.Status = AdcEventStatus.AlarmReset;
                break;
            case AdcRemoteRegister.RemoteStart when value != 0:
                controller.EventCount++;
                controller.ScrewCount++;
                controller.Status = controller.NextStatus;
                controller.NextStatus = AdcEventStatus.FasteningOk;
                break;
            case AdcRemoteRegister.Preset:
                controller.Preset = value;
                controller.Status = AdcEventStatus.PresetChanged;
                break;
            case AdcRemoteRegister.Direction:
                controller.Direction = (AdcDirection)value;
                controller.Status = AdcEventStatus.DirectionChanged;
                break;
        }

        var data = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data, address);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), value);
        var frame = AdcRtuFrame.Build(
            slaveAddress,
            AdcFunctionCode.WriteSingleRegister,
            data);
        Transfer(frame, frame);
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadDeviceInformationAsync(
        byte slaveAddress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = Encoding.ASCII.GetBytes("VIRTUAL ADC");
        Transfer(
            AdcRtuFrame.Build(
                slaveAddress,
                AdcFunctionCode.RequestDeviceInformation,
                []),
            BuildReadResponse(
                slaveAddress,
                AdcFunctionCode.RequestDeviceInformation,
                data));
        return Task.FromResult(data);
    }

    private Task<ushort[]> ReadRegistersAsync(
        byte slaveAddress,
        AdcFunctionCode function,
        ushort address,
        ushort count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var controller = GetController(slaveAddress);
        var values = new ushort[count];
        for (var index = 0; index < count; index++)
        {
            var register = (ushort)(address + index);
            values[index] = function == AdcFunctionCode.ReadInputRegisters
                ? ReadResultRegister(controller, register)
                : controller.Registers.TryGetValue(register, out var value)
                    ? value
                    : (ushort)0;
        }

        var requestData = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(requestData, address);
        BinaryPrimitives.WriteUInt16BigEndian(requestData.AsSpan(2), count);

        var responseData = new byte[count * 2];
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                responseData.AsSpan(index * 2),
                values[index]);
        }

        Transfer(
            AdcRtuFrame.Build(slaveAddress, function, requestData),
            BuildReadResponse(slaveAddress, function, responseData));
        return Task.FromResult(values);
    }

    private Controller GetController(byte slaveAddress) =>
        _controllers.GetOrAdd(slaveAddress, static _ => new Controller());

    private static ushort ReadResultRegister(
        Controller controller,
        ushort address) =>
        (AdcResultRegister)address switch
        {
            AdcResultRegister.EventCount => controller.EventCount,
            AdcResultRegister.FasteningTime => 250,
            AdcResultRegister.Preset => controller.Preset,
            AdcResultRegister.TargetTorque => 100,
            AdcResultRegister.ConvertedTorque => 100,
            AdcResultRegister.TargetSpeed => 1_000,
            AdcResultRegister.ScrewCount => controller.ScrewCount,
            AdcResultRegister.Error => controller.Error,
            AdcResultRegister.Direction => (ushort)controller.Direction,
            AdcResultRegister.Status => (ushort)controller.Status,
            _ => 0,
        };

    private void Transfer(byte[] request, byte[] response)
    {
        FrameTransferred?.Invoke(AdcFrameDirection.Transmit, request);
        FrameTransferred?.Invoke(AdcFrameDirection.Receive, response);
    }

    private static byte[] BuildReadResponse(
        byte slaveAddress,
        AdcFunctionCode function,
        byte[] data)
    {
        var responseData = new byte[data.Length + 1];
        responseData[0] = (byte)data.Length;
        data.CopyTo(responseData, 1);
        return AdcRtuFrame.Build(slaveAddress, function, responseData);
    }

    private sealed class Controller
    {
        public ConcurrentDictionary<ushort, ushort> Registers { get; } = [];
        public ushort EventCount { get; set; }
        public ushort Preset { get; set; }
        public ushort ScrewCount { get; set; }
        public ushort Error { get; set; }
        public AdcDirection Direction { get; set; }
        public AdcEventStatus Status { get; set; }
        public AdcEventStatus NextStatus { get; set; } =
            AdcEventStatus.FasteningOk;
    }
}
