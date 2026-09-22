using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using IBTM.Device;
using IBTM.Core;

namespace IBTM.Virtual;

public sealed class VirtualAdcBus : IAdcBus, IDisposable
{
    private const string VirtualPort = "Virtual";
    private const int FasteningMilliseconds = 250;

    private readonly ConcurrentDictionary<byte, Controller> _controllers;
    private IIoService? _io;

    public VirtualAdcBus()
    {
        _controllers = [];
    }

    public event Action<AdcFrameDirection, byte[]>? FrameTransferred;

    public bool IsOpen { get; private set; }

    public string PortName { get; private set; } = string.Empty;

    public int BaudRate { get; private set; }

    public void SetNextFasteningResult(byte slaveAddress, AdcEventStatus status)
    {
        var controller = GetController(slaveAddress);
        lock (controller)
        {
            controller.NextStatus = status;
        }
    }

    public string[] GetPortNames()
    {
        return [VirtualPort];
    }

    public void Open(string portName, int baudRate)
    {
        var selected = string.IsNullOrWhiteSpace(portName) ? VirtualPort : portName;
        if (IsOpen)
        {
            if (!string.Equals(PortName, selected, StringComparison.OrdinalIgnoreCase) || BaudRate != baudRate)
                throw new InvalidOperationException($"ADC is already connected to {PortName} at {BaudRate} baud.");
            return;
        }
        PortName = selected;
        IsOpen = true;
        BaudRate = baudRate;
    }

    public void Close()
    {
        IsOpen = false;
        PortName = string.Empty;
        BaudRate = 0;
    }

    public Task WriteRegisterAsync(
        byte slaveAddress,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var controller = GetController(slaveAddress);
        ApplyControl(controller, address, value);

        var data = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data, address);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), value);
        var frame = AdcRtuFrame.Build(slaveAddress, AdcFunctionCode.WriteSingleRegister, data);
        Transfer(frame, frame);
        return Task.CompletedTask;
    }

    public VirtualAdcBus(IIoService io, FasteningHead head, byte slaveAddress) : this()
    {
        BindIo(io, head, slaveAddress);
    }

    public void BindIo(IIoService io, FasteningHead head, byte slaveAddress)
    {
        if (_io is not null && !ReferenceEquals(_io, io))
            throw new InvalidOperationException("Virtual ADC is already wired to another I/O service.");
        if (_io is null)
        {
            _io = io;
            _io.OutputChanged += OnOutputChanged;
        }
        var controller = GetController(slaveAddress);
        controller.Head = head;
        controller.Io = io as VirtualIoService;
        UpdateIo(controller);
    }

    public void Dispose()
    {
        if (_io is not null)
            _io.OutputChanged -= OnOutputChanged;
    }

    private void OnOutputChanged(OutputIo output, bool value)
    {
        foreach (var controller in _controllers.Values)
        {
            if (controller.Head is not { } head)
                continue;
            var pickup = head == FasteningHead.Pickup;
            if (output == (pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart))
                ApplyControl(controller, (ushort)AdcRemoteRegister.RemoteStart, (ushort)(value ? 1 : 0));
            else if (output == (pickup ? OutputIo.PickupBoltReset : OutputIo.ShootingBoltReset) && value)
                ApplyControl(controller, (ushort)AdcRemoteRegister.AlarmReset, 1);
            else if (output == (pickup ? OutputIo.PickupBoltDirection : OutputIo.ShootingBoltDirection))
                ApplyControl(controller, (ushort)AdcRemoteRegister.Direction, (ushort)(value ? 1 : 0));
            else if (value)
            {
                OutputIo[] presets = pickup
                    ? [OutputIo.PickupBoltPreset1, OutputIo.PickupBoltPreset2, OutputIo.PickupBoltPreset3]
                    : [OutputIo.ShootingBoltPreset1, OutputIo.ShootingBoltPreset2, OutputIo.ShootingBoltPreset3];
                var index = Array.IndexOf(presets, output);
                if (index >= 0)
                    ApplyControl(controller, (ushort)AdcRemoteRegister.Preset, (ushort)(index + 1));
            }
        }
    }

    private static void UpdateIo(Controller controller)
    {
        if (controller.Io is { } io && controller.Head is { } head)
        {
            var pickup = head == FasteningHead.Pickup;
            io.SetInputs(
                (pickup ? InputIo.PickupBoltFasten : InputIo.ShootingBoltFasten, controller.Running),
                (pickup ? InputIo.PickupBoltReady : InputIo.ShootingBoltReady,
                    !controller.Running && controller.Status != AdcEventStatus.Error),
                (pickup ? InputIo.PickupBoltAlarm : InputIo.ShootingBoltAlarm,
                    controller.Status == AdcEventStatus.Error));
        }
    }

    private static void ApplyControl(Controller controller, ushort address, ushort value)
    {
        lock (controller)
        {
            controller.Registers[address] = value;
            switch ((AdcRemoteRegister)address)
            {
                case AdcRemoteRegister.AlarmReset:
                    controller.Status = AdcEventStatus.AlarmReset;
                    break;
                case AdcRemoteRegister.RemoteStart:
                    if (value != 0)
                        while (controller.AutomaticResults.Reader.TryRead(out _)) { }
                    var version = ++controller.FasteningVersion;
                    controller.Running = value != 0 && controller.Status != AdcEventStatus.Error;
                    if (value != 0 && controller.Status != AdcEventStatus.Error)
                    {
                        controller.Status = AdcEventStatus.None;
                        if (controller.Direction == AdcDirection.Fastening)
                        {
                            var status = controller.NextStatus;
                            controller.NextStatus = AdcEventStatus.FasteningOk;
                            _ = CompleteFasteningAsync(controller, version, status);
                        }
                    }

                    break;
                case AdcRemoteRegister.Preset:
                    controller.Preset = value;
                    if (controller.Status != AdcEventStatus.Error)
                    {
                        controller.Status = AdcEventStatus.PresetChanged;
                    }

                    break;
                case AdcRemoteRegister.Direction:
                    controller.Direction = (AdcDirection)value;
                    if (controller.Status != AdcEventStatus.Error)
                    {
                        controller.Status = AdcEventStatus.DirectionChanged;
                    }

                    break;
            }
        }

        UpdateIo(controller);
    }

    public Task<byte[]> ReadDeviceInformationAsync(
        byte slaveAddress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = Encoding.ASCII.GetBytes("VIRTUAL ADC");
        Transfer(
            AdcRtuFrame.Build(slaveAddress, AdcFunctionCode.RequestDeviceInformation, []),
            BuildReadResponse(slaveAddress, AdcFunctionCode.RequestDeviceInformation, data));
        return Task.FromResult(data);
    }

    public async Task<byte[]> CaptureDeviceInformationAsync(
        byte slaveAddress,
        int durationMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMilliseconds);
        cancellationToken.ThrowIfCancellationRequested();
        var response = BuildReadResponse(
            slaveAddress,
            AdcFunctionCode.RequestDeviceInformation,
            Encoding.ASCII.GetBytes("VIRTUAL ADC"));
        Transfer(
            AdcRtuFrame.Build(slaveAddress, AdcFunctionCode.RequestDeviceInformation, []),
            response);
        await Task.Delay(durationMilliseconds, cancellationToken);
        return response;
    }

    public Task<ushort[]> ReadRegistersAsync(
        byte slaveAddress,
        AdcFunctionCode function,
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var controller = GetController(slaveAddress);
        var values = new ushort[count];
        lock (controller)
        {
            for (var index = 0; index < count; index++)
            {
                var register = (ushort)(address + index);
                values[index] = function == AdcFunctionCode.ReadInputRegisters
                    ? register >= (ushort)AdcStatusRegister.Preset
                        && register <= (ushort)AdcStatusRegister.Direction
                        ? ReadStatusRegister(controller, register)
                        : ReadResultRegister(controller, register)
                    : controller.Registers.TryGetValue(register, out var value) ? value : (ushort)0;
            }
        }

        var requestData = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(requestData, address);
        BinaryPrimitives.WriteUInt16BigEndian(requestData.AsSpan(2), count);

        var responseData = new byte[count * 2];
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(responseData.AsSpan(index * 2), values[index]);
        }

        Transfer(
            AdcRtuFrame.Build(slaveAddress, function, requestData),
            BuildReadResponse(slaveAddress, function, responseData));
        return Task.FromResult(values);
    }

    public async Task<AdcFasteningResult> ReceiveFasteningResultAsync(
        byte slaveAddress,
        CancellationToken cancellationToken = default)
    {
        var values = await GetController(slaveAddress).AutomaticResults.Reader.ReadAsync(cancellationToken);
        var data = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(index * 2), values[index]);
        FrameTransferred?.Invoke(AdcFrameDirection.Receive,
            BuildReadResponse(slaveAddress, AdcFunctionCode.ReadInputRegisters, data));
        return AdcFasteningResult.FromRegisters(values);
    }

    private Controller GetController(byte slaveAddress)
    {
        return _controllers.GetOrAdd(slaveAddress, static _ => new Controller());
    }

    private static async Task CompleteFasteningAsync(
        Controller controller,
        int version,
        AdcEventStatus status)
    {
        await Task.Delay(FasteningMilliseconds).ConfigureAwait(false);
        lock (controller)
        {
            if (controller.FasteningVersion != version)
            {
                return;
            }

            controller.EventCount++;
            controller.ScrewCount++;
            controller.Running = false;
            controller.Status = status;
            var values = new ushort[AdcFasteningResult.RegisterCount];
            for (var index = 0; index < values.Length; index++)
                values[index] = ReadResultRegister(controller, (ushort)((ushort)AdcResultRegister.EventCount + index));
            controller.AutomaticResults.Writer.TryWrite(values);
        }
        UpdateIo(controller);
    }

    private static ushort ReadStatusRegister(Controller controller, ushort address)
    {
        switch ((AdcStatusRegister)address)
        {
            case AdcStatusRegister.Preset:
                return controller.Preset;
            case AdcStatusRegister.Ready:
                return (ushort)(!controller.Running && controller.Status != AdcEventStatus.Error ? 1 : 0);
            case AdcStatusRegister.MotorRun:
                return (ushort)(controller.Running ? 1 : 0);
            case AdcStatusRegister.Alarm:
                return (ushort)(controller.Status == AdcEventStatus.Error ? 1 : 0);
            case AdcStatusRegister.Direction:
                return (ushort)controller.Direction;
            default:
                return 0;
        }
    }

    private static ushort ReadResultRegister(Controller controller, ushort address)
    {
        switch ((AdcResultRegister)address)
        {
            case AdcResultRegister.EventCount:
                return controller.EventCount;
            case AdcResultRegister.FasteningTime:
                return FasteningMilliseconds;
            case AdcResultRegister.Preset:
                return controller.Preset;
            case AdcResultRegister.TargetTorque:
                return 100;
            case AdcResultRegister.ConvertedTorque:
                return 100;
            case AdcResultRegister.TargetSpeed:
                return 1_000;
            case AdcResultRegister.ScrewCount:
                return controller.ScrewCount;
            case AdcResultRegister.Direction:
                return (ushort)controller.Direction;
            case AdcResultRegister.Status:
                return (ushort)controller.Status;
            default:
                return 0;
        }
    }

    private void Transfer(byte[] request, byte[] response)
    {
        FrameTransferred?.Invoke(AdcFrameDirection.Transmit, request);
        FrameTransferred?.Invoke(AdcFrameDirection.Receive, response);
    }

    private static byte[] BuildReadResponse(byte slaveAddress, AdcFunctionCode function, byte[] data)
    {
        return AdcRtuFrame.Build(slaveAddress, function, [(byte)data.Length, .. data]);
    }

    private sealed class Controller
    {
        public Controller()
        {
            Registers = [];
            AutomaticResults = Channel.CreateUnbounded<ushort[]>();
        }

        public FasteningHead? Head { get; set; }
        public VirtualIoService? Io { get; set; }
        public Dictionary<ushort, ushort> Registers { get; }
        public Channel<ushort[]> AutomaticResults { get; }
        public ushort EventCount { get; set; }
        public ushort Preset { get; set; } = 1;
        public ushort ScrewCount { get; set; }
        public AdcDirection Direction { get; set; }
        public AdcEventStatus Status { get; set; }
        public AdcEventStatus NextStatus { get; set; } = AdcEventStatus.FasteningOk;
        public int FasteningVersion { get; set; }
        public bool Running { get; set; }
    }
}
