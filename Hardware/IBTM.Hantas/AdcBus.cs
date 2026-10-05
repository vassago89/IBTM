using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HComm.Common;
using HComm.Device;
using IBTM.Device;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using HantasComm = HComm.HComm;

namespace IBTM.Hantas;

public sealed class AdcBus : IAdcBus, IDisposable
{
    private readonly HantasSettings _settings;
    private readonly ILogger<AdcBus> _logger;
    private readonly SemaphoreSlim _exchange;
    private readonly Lock _stateGate;
    private readonly List<byte> _received;
    private HantasComm? _communication;
    private TaskCompletionSource<AdcResponse>? _pending;
    private Command _command;
    private ushort _address;
    private ushort _countOrValue;
    private byte _slaveAddress;
    private byte[] _transmitted;

    public AdcBus(HantasSettings settings, ILogger<AdcBus>? logger = null,
        ILogger<AdcStatusMonitor>? monitorLogger = null)
    {
        _settings = settings;
        _logger = logger ?? NullLogger<AdcBus>.Instance;
        _exchange = new(1, 1);
        _stateGate = new();
        _received = new();
        _transmitted = [];
        Monitor = new(this, monitorLogger);
    }

    // Tests supply HComm with an in-memory IHComm transport, without opening hardware.
    internal AdcBus(HantasSettings settings, HantasComm communication, byte slaveAddress = 0) : this(settings)
    {
        _slaveAddress = slaveAddress;
        Attach(communication);
    }

    public AdcStatusMonitor Monitor { get; }
    public event Action<AdcFrameDirection, byte[]>? FrameTransferred;
    public bool IsOpen => _communication?.State is ConnectionState.Connecting or ConnectionState.Connected;
    public string PortName { get; private set; } = string.Empty;
    public int BaudRate { get; private set; }
    public string[] PortNames => HcSerial.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public void Open(string portName, int baudRate, byte slaveAddress = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_settings.ResponseTimeoutMilliseconds);
        if (slaveAddress > 15)
            throw new ArgumentOutOfRangeException(nameof(slaveAddress), "HComm serial slave must be 0–15.");
        if (IsOpen)
        {
            if (!string.Equals(PortName, portName, StringComparison.OrdinalIgnoreCase)
                || BaudRate != baudRate || _slaveAddress != slaveAddress)
                throw new InvalidOperationException(
                    $"ADC is connected to {PortName}/{_slaveAddress} at {BaudRate} baud; "
                    + $"requested {portName}/{slaveAddress} at {baudRate} baud. Disconnect first.");
            return;
        }

        Close();
        var communication = new HantasComm();
        communication.SetUp(CommType.Serial);
        Attach(communication);
        PortName = portName;
        BaudRate = baudRate;
        _slaveAddress = slaveAddress;
        if (!communication.Connect(portName, baudRate, slaveAddress))
        {
            Close();
            throw new IOException($"HComm could not open ADC {portName}/{slaveAddress} at {baudRate} baud.");
        }
        _logger.LogInformation("ADC [{Port}/{Slave}] opened with HComm {Version} at {BaudRate} baud.",
            PortName, slaveAddress, typeof(HantasComm).Assembly.GetName().Version, baudRate);
    }

    private void Attach(HantasComm communication)
    {
        // The existing monitor owns acquisition; do not inject idle Info queries into it.
        communication.AutoRequestInfo = false;
        communication.ReceivedMsg = OnReceived;
        communication.SendReceiveMsg = OnSendReceive;
        communication.ChangedConnection = OnConnectionChanged;
        _communication = communication;
    }

    public void Close()
    {
        Monitor.Stop();
        var communication = Interlocked.Exchange(ref _communication, null);
        lock (_stateGate)
            _pending?.TrySetException(new IOException($"ADC {PortName}/{_slaveAddress} disconnected."));
        if (communication is null)
            return;
        communication.ReceivedMsg = null;
        communication.SendReceiveMsg = null;
        communication.ChangedConnection = null;
        if (communication.State is ConnectionState.Connecting or ConnectionState.Connected)
            communication.Close();
    }

    public void Dispose()
    {
        Close();
    }

    public async Task<ushort[]> ReadRegistersAsync(byte slaveAddress, AdcFunctionCode function,
        ushort address, ushort count, CancellationToken cancellationToken = default)
    {
        var command = function switch
        {
            AdcFunctionCode.ReadHoldingRegisters => Command.Read,
            AdcFunctionCode.ReadInputRegisters => Command.Mor,
            _ => throw new ArgumentOutOfRangeException(nameof(function)),
        };
        var response = await ExchangeAsync(slaveAddress, command, address, count, cancellationToken);
        return response.RequireSuccess().Select(value => checked((ushort)value)).ToArray();
    }

    public async Task WriteRegisterAsync(byte slaveAddress, ushort address, ushort value,
        CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync(slaveAddress, Command.Write, address, value, cancellationToken);
        response.RequireSuccess();
    }

    public async Task<byte[]> ReadDeviceInformationAsync(byte slaveAddress,
        CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync(slaveAddress, Command.Info, 0, 0, cancellationToken);
        return response.RequireSuccess().Select(value => checked((byte)value)).ToArray();
    }

    public async Task<byte[]> RequestTorqueCurveAsync(byte slaveAddress,
        CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync(slaveAddress, Command.GraphAd, 4200, 1, cancellationToken);
        response.RequireSuccess();
        return response.Frame;
    }

    public async Task<byte[]> CaptureDeviceInformationAsync(byte slaveAddress, int durationMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMilliseconds);
        var response = await ExchangeAsync(slaveAddress, Command.Info, 0, 0,
            cancellationToken, durationMilliseconds);
        return response.Frame;
    }

    public async Task<(AdcControllerStatus? Status, string? Rejection)> ReadControllerStatusAsync(
        byte slaveAddress, CancellationToken cancellationToken = default)
    {
        // Read HComm's complete default status block (3300..3313).
        var response = await ExchangeAsync(slaveAddress, Command.Mor, 3300, 14, cancellationToken);
        if (response.Rejection is { } rejection && response.ErrorCode is not (0 or 255))
            return (null, rejection);
        var values = response.RequireSuccess().Skip((ushort)AdcStatusRegister.Preset - 3300)
            .Take(AdcControllerStatus.RegisterCount).Select(value => checked((ushort)value)).ToArray();
        return (AdcControllerStatus.FromRegisters(values), null);
    }

    private async Task<AdcResponse> ExchangeAsync(byte slaveAddress, Command command,
        ushort address, ushort countOrValue, CancellationToken cancellationToken, int? captureMilliseconds = null)
    {
        await _exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var communication = _communication;
            if (!IsOpen || communication is null)
                throw new IOException("Hantas ADC is not connected.");
            if (slaveAddress != _slaveAddress)
                throw new InvalidOperationException("Disconnect ADC before changing its slave address.");
            var pending = new TaskCompletionSource<AdcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateGate)
            {
                _command = command;
                _address = address;
                _countOrValue = countOrValue;
                _received.Clear();
                _transmitted = [];
                _pending = pending;
            }
            cancellationToken.ThrowIfCancellationRequested();
            bool accepted;
            switch (command)
            {
                case Command.Read:
                    accepted = communication.GetParam(address, countOrValue, merge: true);
                    break;
                case Command.Mor:
                    accepted = communication.GetState(address, countOrValue);
                    break;
                case Command.Write:
                    accepted = communication.SetParam(address, countOrValue);
                    break;
                case Command.Info:
                    accepted = communication.GetInfo();
                    break;
                case Command.GraphAd:
                    accepted = communication.GetGraph(4200, 1);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command));
            }
            if (!accepted)
                throw new IOException($"HComm did not queue {command}, address={address}, data={countOrValue}.");
            var startedAt = Stopwatch.GetTimestamp();

            // HComm owns its 1 s response timeout. Drain an accepted request before cancellation
            // releases this bus, so its late response cannot become the next request's result.
            AdcResponse response;
            try
            {
                response = await pending.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Close();
                throw new TimeoutException($"HComm did not finish ADC {PortName}/{slaveAddress} {command}; connection closed.");
            }
            if (captureMilliseconds is { } duration)
            {
                var remaining = TimeSpan.FromMilliseconds(duration) - Stopwatch.GetElapsedTime(startedAt);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                lock (_stateGate)
                    response = response with { Frame = _received.ToArray() };
            }
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        finally
        {
            lock (_stateGate)
                _pending = null;
            _exchange.Release();
        }
    }

    private void OnSendReceive(byte[] packet, bool send)
    {
        lock (_stateGate)
        {
            if (send)
                _transmitted = packet.ToArray();
            else if (_pending is not null)
                _received.AddRange(packet);
        }
        FrameTransferred?.Invoke(send ? AdcFrameDirection.Transmit : AdcFrameDirection.Receive, packet);
        _logger.LogInformation("ADC [{Port}] HComm {Direction} {Frame}",
            PortName, send ? "TX" : "RX RAW", Convert.ToHexString(packet));
    }

    private void OnReceived(Command command, int address, int[]? values)
    {
        lock (_stateGate)
        {
            _logger.LogDebug("ADC [{Port}] HComm decoded {Command}, address={Address}, values={Values}.",
                PortName, command, address, values is null ? "null" : string.Join(",", values));
            if (command == Command.GraphAd && _command != Command.GraphAd)
            {
                Monitor.ReceiveTorqueCurveFrame(_received.ToArray());
                return;
            }
            if (_pending is null || _pending.Task.IsCompleted)
                return;
            byte? errorCode = null;
            string? rejection = null;
            if (command == Command.Error)
            {
                errorCode = values is { Length: > 0 } ? checked((byte)values[0]) : null;
                rejection = errorCode switch
                {
                    0 => "HComm response timeout (0x00)",
                    255 => "HComm CRC error (0xFF)",
                    { } code => $"HComm controller rejection (0x{code:X2})",
                    null => "HComm error: missing error code",
                };
            }
            else if (_received.Count == 0 || _received[0] != _slaveAddress
                || command != _command || values is null
                || (command is Command.Read or Command.Mor && values.Length != _countOrValue)
                || (command == Command.Write
                    && (values.Length != 2 || values[0] != _address || values[1] != _countOrValue)))
                rejection = $"HComm reply mismatch: received {command}, address={address}, values={values?.Length}";
            if (rejection is not null)
            {
                rejection += $"; request={_command}, address={_address}, data={_countOrValue}; "
                    + $"TX={Convert.ToHexString(_transmitted)}; RX={Convert.ToHexString(_received.ToArray())}.";
                _logger.LogWarning("ADC [{Port}/{Slave}] {Rejection}", PortName, _slaveAddress, rejection);
            }
            _pending.TrySetResult(new(values ?? [], _received.ToArray(), rejection, errorCode));
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        _logger.LogInformation("ADC [{Port}] HComm connection={Connected}.", PortName, connected);
        if (connected)
            return;
        lock (_stateGate)
            _pending?.TrySetException(new IOException($"HComm ADC {PortName}/{_slaveAddress} disconnected."));
        Monitor.Stop();
    }
}

internal sealed record AdcResponse(int[] Values, byte[] Frame, string? Rejection = null, byte? ErrorCode = null)
{
    public int[] RequireSuccess()
    {
        if (Rejection is { } rejection)
        {
            if (ErrorCode == 0)
                throw new TimeoutException(rejection);
            if (ErrorCode == 255)
                throw new InvalidDataException(rejection);
            if (ErrorCode is { } code)
                throw new AdcResponseException(code, rejection);
            throw new AdcUnexpectedResponseException(rejection);
        }
        return Values;
    }
}

