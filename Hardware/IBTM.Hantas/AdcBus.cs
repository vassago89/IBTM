using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using IBTM.Device;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IBTM.Hantas;

public sealed class AdcBus : IAdcBus, IDisposable
{
    private readonly HantasSettings _settings;
    private readonly ILogger<AdcBus> _logger;
    private const byte ExceptionFunctionMask = 0x80;

    private readonly SemaphoreSlim _exchange;
    private readonly Lock _receiveLock;
    private readonly List<byte> _receiveBuffer;
    private Channel<byte[]> _fasteningResults;
    private PendingResponse? _response;
    private PendingResponse? _frameResponse;
    private SerialPort? _port;

    public AdcBus(HantasSettings settings, ILogger<AdcBus>? logger = null)
    {
        _settings = settings;
        _logger = logger ?? NullLogger<AdcBus>.Instance;
        _exchange = new(1, 1);
        _receiveLock = new();
        _receiveBuffer = [];
        _fasteningResults = Channel.CreateUnbounded<byte[]>();
    }

    public event Action<AdcFrameDirection, byte[]>? FrameTransferred;

    public bool IsOpen => _port?.IsOpen == true;

    public string PortName => _port?.PortName ?? string.Empty;

    public int BaudRate => _port?.BaudRate ?? 0;

    public string[] GetPortNames()
    {
        return SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Open(string portName, int baudRate)
    {
        if (IsOpen)
        {
            VerifyConnectionSettings(_port!, portName, baudRate);
            return;
        }

        Close();
        _port = new SerialPort(portName, baudRate, Parity.None, dataBits: 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = _settings.ResponseTimeoutMilliseconds,
            WriteTimeout = _settings.ResponseTimeoutMilliseconds,
        };
        try
        {
            lock (_receiveLock)
                _fasteningResults = Channel.CreateUnbounded<byte[]>();
            _port.DataReceived += OnDataReceived;
            _port.Open();
        }
        catch
        {
            Close();
            throw;
        }
    }

    public void Close()
    {
        SerialPort? port;
        lock (_receiveLock)
        {
            port = _port;
            _port = null;
            if (port is not null)
                port.DataReceived -= OnDataReceived;
            var error = new IOException("ADC serial connection closed.");
            _response?.Completion.TrySetException(error);
            _response = null;
            _frameResponse = null;
            _receiveBuffer.Clear();
            _fasteningResults.Writer.TryComplete(error);
        }
        // Dispose outside the receive lock: SerialPort waits for an active event handler.
        port?.Dispose();
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        lock (_receiveLock)
        {
            if (sender is not SerialPort port || !ReferenceEquals(port, _port))
                return;
            try
            {
                while (port.BytesToRead > 0)
                {
                    var bytes = new byte[Math.Min(512, port.BytesToRead)];
                    var count = port.Read(bytes, 0, bytes.Length);
                    if (count == 0)
                        break;
                    ReceiveBytes(count == bytes.Length ? bytes : bytes[..count]);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "ADC {Port} DataReceived failed.", port.PortName);
                _response?.Completion.TrySetException(exception);
                _fasteningResults.Writer.TryComplete(exception);
            }
        }
    }

    // The same byte-receive boundary is used by protocol tests, without opening hardware.
    internal void ReceiveBytes(byte[] bytes)
    {
        lock (_receiveLock)
        {
            // Log actual event chunks before interpreting their contents.
            FrameTransferred?.Invoke(AdcFrameDirection.Receive, bytes);
            if (_response is { } receiving)
            {
                receiving.Bytes.AddRange(bytes);
                receiving.Chunks++;
                if (receiving.Capture)
                    return;
            }

            if (_receiveBuffer.Count == 0)
                _frameResponse = _response;
            _receiveBuffer.AddRange(bytes);
            while (_receiveBuffer.Count >= 2)
            {
                var function = _receiveBuffer[1];
                var length = ResponseLength(_receiveBuffer);
                if (length == 0 || _receiveBuffer.Count < length)
                    return;

                var frame = _receiveBuffer.GetRange(0, length).ToArray();
                _receiveBuffer.RemoveRange(0, length);
                var pending = _frameResponse;
                var automatic = function == (byte)AdcFunctionCode.ReadInputRegisters
                    && frame[2] == AdcFasteningResult.RegisterCount * 2;
                if (pending is not null && !pending.Completion.Task.IsCompleted)
                {
                    try
                    {
                        if (automatic && (pending.Function != AdcFunctionCode.ReadInputRegisters
                            || pending.ByteCount != frame[2]))
                        {
                            ValidateFrame(frame, pending.SlaveAddress, function);
                            _fasteningResults.Writer.TryWrite(frame);
                        }
                        else
                        {
                            ValidateResponse(frame, pending.SlaveAddress, pending.Function, pending.ByteCount);
                            _logger.LogDebug("ADC {Port} RTU response: {Interpretation}", PortName, DescribeResponse(frame));
                            pending.Completion.TrySetResult(frame);
                        }
                    }
                    catch (Exception exception)
                    {
                        pending.Completion.TrySetException(exception);
                    }
                }
                else if (automatic)
                {
                    _fasteningResults.Writer.TryWrite(frame);
                }
                else
                {
                    _logger.LogWarning("ADC unsolicited / expired response: RX={Frame}.", Convert.ToHexString(frame));
                }
                _frameResponse = _response;
            }
        }
    }

    internal PendingResponse BeginResponse(byte slaveAddress, AdcFunctionCode function,
        int? byteCount = null, bool capture = false)
    {
        lock (_receiveLock)
        {
            if (_response is { Completion.Task.IsCompleted: false })
                throw new InvalidOperationException("An ADC response is already pending.");
            _response = new(slaveAddress, function, byteCount, capture);
            if (capture)
            {
                _receiveBuffer.Clear();
                _frameResponse = null;
            }
            return _response;
        }
    }

    internal async Task<byte[]> WaitForResponseAsync(PendingResponse response,
        CancellationToken cancellationToken, int? captureMilliseconds = null)
    {
        try
        {
            if (captureMilliseconds is not { } duration)
                return await response.Completion.Task.WaitAsync(cancellationToken);
            try
            {
                return await response.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(duration), cancellationToken);
            }
            catch (TimeoutException)
            {
                lock (_receiveLock)
                    return response.Bytes.ToArray();
            }
        }
        finally
        {
            lock (_receiveLock)
            {
                response.Completion.TrySetCanceled();
                if (ReferenceEquals(_response, response))
                    _response = null;
            }
        }
    }

    internal static void VerifyConnectionSettings(SerialPort port, string portName, int baudRate)
    {
        if (!string.Equals(port.PortName, portName, StringComparison.OrdinalIgnoreCase)
            || port.BaudRate != baudRate)
        {
            throw new InvalidOperationException(
                $"ADC is connected to {port.PortName} at {port.BaudRate} baud; "
                + $"requested {portName} at {baudRate} baud. Disconnect the current connection first.");
        }
    }

    public async Task WriteRegisterAsync(
        byte slaveAddress,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data, address);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), value);
        var request = AdcRtuFrame.Build(slaveAddress, AdcFunctionCode.WriteSingleRegister, data);
        var response = await ExchangeAsync(
            slaveAddress,
            AdcFunctionCode.WriteSingleRegister,
            request,
            cancellationToken);

        if (!request.AsSpan(0, 6).SequenceEqual(response.AsSpan(0, 6)))
        {
            throw new InvalidDataException(
                $"ADC write response does not match the request; TX={Convert.ToHexString(request)}; RX={Convert.ToHexString(response)}.");
        }
    }

    public async Task<byte[]> ReadDeviceInformationAsync(
        byte slaveAddress,
        CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync(
            slaveAddress,
            AdcFunctionCode.RequestDeviceInformation,
            AdcRtuFrame.Build(slaveAddress, AdcFunctionCode.RequestDeviceInformation, []),
            cancellationToken);
        return response[3..^2];
    }

    public Task<byte[]> CaptureDeviceInformationAsync(
        byte slaveAddress,
        int durationMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMilliseconds);
        return ExchangeAsync(
            slaveAddress,
            AdcFunctionCode.RequestDeviceInformation,
            AdcRtuFrame.Build(slaveAddress, AdcFunctionCode.RequestDeviceInformation, []),
            cancellationToken,
            durationMilliseconds);
    }

    public void Dispose()
    {
        try
        {
            Close();
        }
        finally
        {
            _exchange.Dispose();
        }
    }

    public async Task<ushort[]> ReadRegistersAsync(
        byte slaveAddress,
        AdcFunctionCode function,
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data, address);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), count);
        var response = await ExchangeAsync(
            slaveAddress,
            function,
            AdcRtuFrame.Build(slaveAddress, function, data),
            cancellationToken,
            expectedByteCount: count * 2);
        return DecodeRegisters(response, count);
    }

    public async Task<AdcFasteningResult> ReceiveFasteningResultAsync(
        byte slaveAddress,
        CancellationToken cancellationToken = default)
    {
        await _exchange.WaitAsync(cancellationToken);
        try
        {
            var port = _port;
            if (port?.IsOpen != true)
                throw new InvalidOperationException("Hantas ADC is not connected. Open the configured COM port first.");

            return await WaitForFasteningResultAsync(slaveAddress, cancellationToken);
        }
        finally
        {
            _exchange.Release();
        }
    }

    internal async Task<AdcFasteningResult> WaitForFasteningResultAsync(
        byte slaveAddress, CancellationToken cancellationToken)
    {
        Channel<byte[]> results;
        lock (_receiveLock)
            results = _fasteningResults;
        var response = await results.Reader.ReadAsync(cancellationToken);
        ValidateResponse(response, slaveAddress, AdcFunctionCode.ReadInputRegisters,
            AdcFasteningResult.RegisterCount * 2);
        return AdcFasteningResult.FromRegisters(DecodeRegisters(response, AdcFasteningResult.RegisterCount));
    }

    private static ushort[] DecodeRegisters(byte[] response, ushort count)
    {
        var byteCount = response[2];
        if (byteCount != count * 2)
        {
            throw new InvalidDataException($"ADC returned {byteCount} data bytes; expected {count * 2}.");
        }

        var values = new ushort[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(3 + index * 2, 2));
        }

        return values;
    }

    private async Task<byte[]> ExchangeAsync(
        byte slaveAddress,
        AdcFunctionCode function,
        byte[] request,
        CancellationToken cancellationToken,
        int? captureMilliseconds = null,
        int? expectedByteCount = null)
    {
        await _exchange.WaitAsync(cancellationToken);
        try
        {
            var port = _port;
            if (port?.IsOpen != true)
                throw new InvalidOperationException("Hantas ADC is not connected. Open the configured COM port first.");
            // Keep the bus owned between frames. 8N1 uses 10 bits per character;
            // allow at least 3.5 characters, with a conservative 2 ms minimum at higher baud rates.
            var frameGapMilliseconds = Math.Max(2, (int)Math.Ceiling(35_000.0 / port.BaudRate));
            await Task.Delay(frameGapMilliseconds, cancellationToken);
            if (function == AdcFunctionCode.WriteSingleRegister
                && BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2)) == (ushort)AdcRemoteRegister.RemoteStart)
            {
                // Results belong only to this START, never to a later restart.
                while (_fasteningResults.Reader.TryRead(out _)) { }
            }
            if (function == AdcFunctionCode.ReadInputRegisters
                && BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2)) == (ushort)AdcResultRegister.EventCount)
            {
                while (_fasteningResults.Reader.TryRead(out _)) { }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_settings.ResponseTimeoutMilliseconds);
            var started = Stopwatch.GetTimestamp();
            var pending = BeginResponse(slaveAddress, function, expectedByteCount, captureMilliseconds is not null);
            byte[] response;
            try
            {
                FrameTransferred?.Invoke(AdcFrameDirection.Transmit, request);
                await AwaitSerialIoAsync(
                    port.BaseStream.WriteAsync(request, timeout.Token).AsTask(),
                    port.DiscardOutBuffer,
                    timeout.Token);
                if (captureMilliseconds is not null)
                    timeout.CancelAfter(Timeout.Infinite);
                response = await WaitForResponseAsync(pending, timeout.Token, captureMilliseconds);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                byte[] receivedBytes;
                int receivedChunks;
                lock (_receiveLock)
                {
                    receivedBytes = pending.Bytes.ToArray();
                    receivedChunks = pending.Chunks;
                }
                var detail = $"ADC {port.PortName}/{slaveAddress}; baud={port.BaudRate}; "
                    + $"elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms; "
                    + $"TX={Convert.ToHexString(request)}; RX ALL={Convert.ToHexString(receivedBytes)}; "
                    + $"RX chunks={receivedChunks}, bytes={receivedBytes.Length}.";
                if (exception is AdcUnexpectedResponseException unexpected)
                    throw new AdcUnexpectedResponseException($"{detail} {unexpected.Message}", unexpected);
                _logger.LogError(exception, "ADC exchange failed. {Detail}", detail);
                if (exception is AdcResponseException rejection)
                    throw new AdcResponseException(rejection.ErrorCode, $"{detail} {rejection.Message}", rejection);
                if (exception is InvalidDataException invalid)
                    throw new InvalidDataException($"{detail} {invalid.Message}", invalid);
                if (exception is OperationCanceledException)
                    throw new TimeoutException(
                        $"{detail} Response timed out after {_settings.ResponseTimeoutMilliseconds} ms.", exception);
                throw;
            }

            return response;
        }
        finally
        {
            lock (_receiveLock)
            {
                _response?.Completion.TrySetCanceled();
                _response = null;
            }
            _exchange.Release();
        }
    }

    private static int ResponseLength(IReadOnlyList<byte> bytes)
    {
        if (bytes.Count < 2)
            return 0;
        var function = bytes[1];
        if ((function & ExceptionFunctionMask) != 0)
            return 5;
        switch (function)
        {
            case 0x05 or 0x06 or 0x08 or 0x0B or 0x0F or 0x10:
                return 8;
            case 0x07:
                return 5;
            case 0x16:
                return 10;
            case 0x18:
                return bytes.Count < 4 ? 0 : (bytes[2] << 8 | bytes[3]) + 6;
            case 0x2B when bytes.Count >= 3 && bytes[2] == 0x0E:
                // Read Device Identification: ID/length/value entries after the object count.
                if (bytes.Count < 8)
                    return 0;
                var length = 8;
                for (var index = 0; index < bytes[7]; index++)
                {
                    if (bytes.Count < length + 2)
                        return 0;
                    length += bytes[length + 1] + 2;
                }
                return length + 2;
            default:
                // Read responses use one byte count; unknown functions are tested against this shape.
                return bytes.Count < 3 ? 0 : bytes[2] + 5;
        }
    }

    internal static void ValidateResponse(byte[] frame, byte slaveAddress,
        AdcFunctionCode function, int? expectedByteCount = null)
    {
        // Integrity and request ownership are separate: a different function is not a broken frame.
        ValidateFrame(frame, slaveAddress, frame.Length >= 2 ? frame[1] : (byte)function);
        var isException = (frame[1] & ExceptionFunctionMask) != 0;
        var expectedFunction = isException ? (byte)((byte)function | ExceptionFunctionMask) : (byte)function;
        if (frame[1] != expectedFunction)
        {
            throw new AdcUnexpectedResponseException(
                $"ADC response does not match request function=0x{(byte)function:X2}; "
                + $"expected response=0x{expectedFunction:X2}. {DescribeResponse(frame)}");
        }
        if (isException)
        {
            var code = (AdcExceptionCode)frame[2];
            throw new AdcResponseException(frame[2],
                $"ADC controller returned {code} (0x{(byte)code:X2}). {DescribeResponse(frame)}");
        }
        if (expectedByteCount is { } expected && frame[2] != expected)
            throw new AdcUnexpectedResponseException(
                $"ADC returned {frame[2]} data bytes; expected {expected}. {DescribeResponse(frame)}");
    }

    internal static string DescribeResponse(byte[] frame)
    {
        var isException = (frame[1] & ExceptionFunctionMask) != 0;
        var function = (byte)(frame[1] & ~ExceptionFunctionMask);
        var name = function switch
        {
            0x01 => "Read Coils",
            0x02 => "Read Discrete Inputs",
            0x03 => "Read Holding Registers",
            0x04 => "Read Input Registers",
            0x05 => "Write Single Coil",
            0x06 => "Write Single Register",
            0x07 => "Read Exception Status",
            0x08 => "Diagnostics",
            0x0B => "Get Comm Event Counter",
            0x0C => "Get Comm Event Log",
            0x0F => "Write Multiple Coils",
            0x10 => "Write Multiple Registers",
            0x11 => "Report Server ID",
            0x14 => "Read File Record",
            0x15 => "Write File Record",
            0x16 => "Mask Write Register",
            0x17 => "Read/Write Multiple Registers",
            0x18 => "Read FIFO Queue",
            0x2B => "Encapsulated Interface Transport",
            _ => "Vendor-specific / unspecified function",
        };
        var interpretation = $"Modbus RTU interpretation: address={frame[0]}, function=0x{frame[1]:X2}, "
            + $"base function=0x{function:X2} ({name}), kind={(isException ? "exception" : "normal")}";
        if (isException)
        {
            var meaning = frame[2] switch
            {
                0x01 => "Illegal Function",
                0x02 => "Illegal Data Address",
                0x03 => "Illegal Data Value",
                0x04 => "Server Device Failure",
                0x05 => "Acknowledge",
                0x06 => "Server Device Busy",
                0x08 => "Memory Parity Error",
                0x0A => "Gateway Path Unavailable",
                0x0B => "Gateway Target Device Failed to Respond",
                _ => "Vendor-specific / unspecified exception",
            };
            interpretation += $", exception=0x{frame[2]:X2} ({meaning}); ADC firmware meaning unconfirmed";
        }
        else
            interpretation += $", data={Convert.ToHexString(frame.AsSpan(2, frame.Length - 4))}";
        return $"{interpretation}; CRC valid; RX={Convert.ToHexString(frame)}.";
    }

    internal sealed class PendingResponse
    {
        public PendingResponse(byte slaveAddress, AdcFunctionCode function, int? byteCount, bool capture)
        {
            SlaveAddress = slaveAddress;
            Function = function;
            ByteCount = byteCount;
            Capture = capture;
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Bytes = [];
        }

        public byte SlaveAddress { get; }
        public AdcFunctionCode Function { get; }
        public int? ByteCount { get; }
        public bool Capture { get; }
        public TaskCompletionSource<byte[]> Completion { get; }
        public List<byte> Bytes { get; }
        public int Chunks { get; set; }
    }

    internal static async Task AwaitSerialIoAsync(
        Task operation,
        Action abort,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Windows SerialStream may ignore cancellation during a native write.
            // Drain the aborted write before the next request owns the bus.
            try
            {
                abort();
            }
            finally
            {
                await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            throw;
        }
    }

    private static void ValidateFrame(byte[] frame, byte slaveAddress, byte function)
    {
        if (frame.Length is < 5 or > 256 || (frame[1] & ~ExceptionFunctionMask) == 0
            || ResponseLength(frame) != frame.Length)
            throw new InvalidDataException($"Invalid Modbus RTU response shape; RX={Convert.ToHexString(frame)}.");
        var receivedCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(^2));
        var calculatedCrc = AdcRtuFrame.CalculateCrc(frame.AsSpan(0, frame.Length - 2));
        if (receivedCrc != calculatedCrc)
        {
            throw new InvalidDataException(
                $"ADC response CRC is invalid: received=0x{receivedCrc:X4}, calculated=0x{calculatedCrc:X4}; "
                + $"RX={Convert.ToHexString(frame)}; expected address={slaveAddress}, function=0x{function:X2}.");
        }
        if (frame[0] != slaveAddress || frame[1] != function)
        {
            throw new InvalidDataException(
                $"ADC response address={frame[0]}, function=0x{frame[1]:X2}; "
                + $"expected address={slaveAddress}, function=0x{function:X2}; "
                + $"CRC valid (0x{receivedCrc:X4}); RX={Convert.ToHexString(frame)}.");
        }
    }

    private enum AdcExceptionCode : byte
    {
        IllegalFunction = 0x01,
        IllegalAddress = 0x02,
        InvalidDataLength = 0x03,
        InvalidCrc = 0x07,
        ByteCountExceeded = 0x0C,
        ValueOutOfRange = 0x0E,
    }
}
