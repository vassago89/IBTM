using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private SerialPort? _port;

    public AdcBus(HantasSettings settings, ILogger<AdcBus>? logger = null)
    {
        Monitor = new(this);
        _settings = settings;
        _logger = logger ?? NullLogger<AdcBus>.Instance;
        _exchange = new(1, 1);
    }

    public AdcStatusMonitor Monitor { get; }

    public event Action<AdcFrameDirection, byte[]>? FrameTransferred;


    public bool IsOpen => _port?.IsOpen == true;

    public string PortName => _port?.PortName ?? string.Empty;

    public int BaudRate => _port?.BaudRate ?? 0;

    public string[] PortNames => SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();

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
        Monitor.Stop();
        // Disposing the port also releases an exchange waiting in its native read/write.
        var port = Interlocked.Exchange(ref _port, null);
        port?.Dispose();
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
        Close();
        // Cancelled exchanges still release this managed gate while unwinding.
        // No wait handle is allocated, so leave disposal to garbage collection.
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
            // No unsolicited data is used. Start each request without leftovers from an expired exchange.
            if (port.BytesToRead > 0)
            {
                _logger.LogWarning("ADC [{Port}] discarding {Count} stale bytes before TX.", port.PortName, port.BytesToRead);
                port.DiscardInBuffer();
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_settings.ResponseTimeoutMilliseconds);
            var started = Stopwatch.GetTimestamp();
            var receivedBytes = new List<byte>();
            var receivedChunks = 0;
            void OnReceived(byte[] bytes)
            {
                receivedBytes.AddRange(bytes);
                receivedChunks++;
                FrameTransferred?.Invoke(AdcFrameDirection.Receive, bytes);
                _logger.LogInformation("ADC [{Port}] RX RAW {Frame}", port.PortName, Convert.ToHexString(bytes));
            }
            byte[] response;
            try
            {
                FrameTransferred?.Invoke(AdcFrameDirection.Transmit, request);
                _logger.LogInformation("ADC [{Port}] TX {Frame}", port.PortName, Convert.ToHexString(request));
                await AwaitSerialIoAsync(
                    port.BaseStream.WriteAsync(request, timeout.Token).AsTask(),
                    port.DiscardOutBuffer,
                    timeout.Token);
                if (captureMilliseconds is not null)
                    timeout.CancelAfter(Timeout.Infinite);
                response = await ReadResponseAsync(port.BaseStream, port.DiscardInBuffer, OnReceived,
                    slaveAddress, function, timeout.Token, expectedByteCount, captureMilliseconds);
                if (captureMilliseconds is null)
                    _logger.LogDebug("ADC {Port} RTU response: {Interpretation}", port.PortName, DescribeResponse(response));
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var detail = $"ADC {port.PortName}/{slaveAddress}; baud={port.BaudRate}; "
                    + $"elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms; "
                    + $"TX={Convert.ToHexString(request)}; RX ALL={Convert.ToHexString(receivedBytes.ToArray())}; "
                    + $"RX chunks={receivedChunks}, bytes={receivedBytes.Count}.";
                if (exception is AdcUnexpectedResponseException unexpected)
                    throw new AdcUnexpectedResponseException($"{detail} {unexpected.Message}", unexpected);
                if (exception is AdcResponseException rejection)
                {
                    _logger.LogWarning(exception, "ADC request rejected. {Detail}", detail);
                    throw new AdcResponseException(rejection.ErrorCode, $"{detail} {rejection.Message}", rejection);
                }
                _logger.LogError(exception, "ADC exchange failed. {Detail}", detail);
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
        if (frame.Length is < 5 or > 256 || (frame[1] & ~ExceptionFunctionMask) == 0
            || ResponseLength(frame) != frame.Length)
            throw new InvalidDataException($"Invalid Modbus RTU response shape; RX={Convert.ToHexString(frame)}.");
        var receivedCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(^2));
        var calculatedCrc = AdcRtuFrame.CalculateCrc(frame.AsSpan(0, frame.Length - 2));
        if (receivedCrc != calculatedCrc)
        {
            throw new InvalidDataException(
                $"ADC response CRC is invalid: received=0x{receivedCrc:X4}, calculated=0x{calculatedCrc:X4}; "
                + $"RX={Convert.ToHexString(frame)}; expected address={slaveAddress}, request function=0x{(byte)function:X2}.");
        }
        if (frame[0] != slaveAddress)
        {
            throw new InvalidDataException(
                $"ADC response address={frame[0]}, function=0x{frame[1]:X2}; "
                + $"expected address={slaveAddress}, request function=0x{(byte)function:X2}; "
                + $"CRC valid (0x{receivedCrc:X4}); RX={Convert.ToHexString(frame)}.");
        }

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

    // Read only this request's response; fragmented serial reads are joined up to the RTU frame length.
    internal static async Task<byte[]> ReadResponseAsync(
        Stream stream,
        Action abortRead,
        Action<byte[]> received,
        byte slaveAddress,
        AdcFunctionCode function,
        CancellationToken cancellationToken,
        int? expectedByteCount = null,
        int? captureMilliseconds = null)
    {
        var bytes = new List<byte>();
        var buffer = new byte[256];
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (captureMilliseconds is { } duration)
            capture.CancelAfter(duration);
        try
        {
            while (true)
            {
                capture.Token.ThrowIfCancellationRequested();
                var length = captureMilliseconds is null ? ResponseLength(bytes) : 0;
                if (length > buffer.Length)
                    throw new InvalidDataException($"ADC response exceeds the RTU frame limit; RX={Convert.ToHexString(bytes.ToArray())}.");
                if (captureMilliseconds is null && length > 0 && bytes.Count == length)
                {
                    var frame = bytes.ToArray();
                    ValidateResponse(frame, slaveAddress, function, expectedByteCount);
                    return frame;
                }
                var remaining = captureMilliseconds is not null ? buffer.Length
                    : length > 0 ? length - bytes.Count : bytes.Count < 2 ? 2 - bytes.Count : 1;
                var reading = stream.ReadAsync(buffer.AsMemory(0, remaining), capture.Token).AsTask();
                await AwaitSerialIoAsync(reading, abortRead, capture.Token).ConfigureAwait(false);
                var count = await reading.ConfigureAwait(false);
                if (count == 0)
                    throw new EndOfStreamException("ADC connection ended before the response was complete.");
                var chunk = buffer[..count];
                received(chunk);
                bytes.AddRange(chunk);
            }
        }
        catch (OperationCanceledException) when (captureMilliseconds is not null && !cancellationToken.IsCancellationRequested)
        {
            return bytes.ToArray();
        }
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
            // Windows SerialStream may ignore cancellation during native I/O.
            // Drain the aborted read/write before the next request owns the bus.
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
