using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Hantas;

public sealed class AdcBus(HantasSettings settings) : IAdcBus, IDisposable
{
    private const byte ExceptionFunctionMask = 0x80;

    private readonly SemaphoreSlim _exchange = new(1, 1);
    private SerialPort? _port;

    public bool IsOpen
    {
        get
        {
            return _port?.IsOpen == true;
        }
    }

    public string PortName
    {
        get
        {
            return _port?.PortName ?? string.Empty;
        }
    }

    public int BaudRate
    {
        get
        {
            return _port?.BaudRate ?? 0;
        }
    }

    public event Action<AdcFrameDirection, byte[]>? FrameTransferred;

    public string[] GetPortNames()
    {
        return SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Open(string portName, int baudRate)
    {
        if (IsOpen)
        {
            return;
        }

        Close();
        _port = new SerialPort(portName, baudRate, Parity.None, dataBits: 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = settings.ResponseTimeoutMilliseconds,
            WriteTimeout = settings.ResponseTimeoutMilliseconds,
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
        var port = _port;
        _port = null;
        port?.Dispose();
    }

    public Task<ushort[]> ReadHoldingRegistersAsync(
        byte slaveAddress,
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default)
    {
        return ReadRegistersAsync(
            slaveAddress,
            AdcFunctionCode.ReadHoldingRegisters,
            address,
            count,
            cancellationToken);
    }

    public Task<ushort[]> ReadInputRegistersAsync(
        byte slaveAddress,
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default)
    {
        return ReadRegistersAsync(
            slaveAddress,
            AdcFunctionCode.ReadInputRegisters,
            address,
            count,
            cancellationToken);
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
            throw new InvalidDataException("ADC write response does not match the request.");
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

    public void Dispose()
    {
        Close();
        _exchange.Dispose();
    }

    private async Task<ushort[]> ReadRegistersAsync(
        byte slaveAddress,
        AdcFunctionCode function,
        ushort address,
        ushort count,
        CancellationToken cancellationToken)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data, address);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), count);
        var response = await ExchangeAsync(
            slaveAddress,
            function,
            AdcRtuFrame.Build(slaveAddress, function, data),
            cancellationToken);
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
        CancellationToken cancellationToken)
    {
        await _exchange.WaitAsync(cancellationToken);
        try
        {
            var port = _port;
            if (port?.IsOpen != true)
                throw new InvalidOperationException("Hantas ADC is not connected. Open the configured COM port first.");
            port.DiscardInBuffer();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.ResponseTimeoutMilliseconds);

            byte[] response;
            try
            {
                FrameTransferred?.Invoke(AdcFrameDirection.Transmit, request);
                await AwaitSerialIoAsync(
                    port.BaseStream.WriteAsync(request, timeout.Token).AsTask(),
                    port.DiscardOutBuffer,
                    timeout.Token);
                response = await ReadResponseAsync(
                    port.BaseStream,
                    port.DiscardInBuffer,
                    bytes => FrameTransferred?.Invoke(AdcFrameDirection.Receive, bytes),
                    slaveAddress,
                    function,
                    timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"ADC {slaveAddress} response timed out after {settings.ResponseTimeoutMilliseconds} ms.");
            }

            return response;
        }
        finally
        {
            _exchange.Release();
        }
    }

    internal static async Task<byte[]> ReadResponseAsync(
        Stream stream,
        Action abortRead,
        Action<byte[]> received,
        byte slaveAddress,
        AdcFunctionCode function,
        CancellationToken cancellationToken)
    {
        Task ReadAsync(byte[] bytes)
        {
            return AwaitSerialIoAsync(ReadAndReportAsync(bytes), abortRead, cancellationToken);
        }

        async Task ReadAndReportAsync(byte[] bytes)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    throw new EndOfStreamException("ADC response ended before the frame was complete.");
                // Report bytes before parsing, including partial and invalid responses.
                received(bytes.AsSpan(offset, count).ToArray());
                offset += count;
            }
        }

        var header = new byte[2];
        await ReadAsync(header);

        if ((header[1] & ExceptionFunctionMask) != 0)
        {
            var tail = new byte[3];
            await ReadAsync(tail);
            byte[] errorFrame = [.. header, .. tail];
            ValidateFrame(errorFrame, slaveAddress, (byte)((byte)function | ExceptionFunctionMask));
            var code = (AdcExceptionCode)tail[0];
            throw new IOException($"ADC controller returned {code} (0x{(byte)code:X2}).");
        }

        byte[] response;
        if (header[1] == (byte)AdcFunctionCode.WriteSingleRegister)
        {
            var tail = new byte[6];
            await ReadAsync(tail);
            response = [.. header, .. tail];
        }
        else
        {
            var count = new byte[1];
            await ReadAsync(count);
            var byteCount = count[0];
            var tail = new byte[byteCount + 2];
            await ReadAsync(tail);
            response = [.. header, byteCount, .. tail];
        }

        ValidateFrame(response, slaveAddress, (byte)function);
        return response;
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
            // Windows SerialStream ignores cancellation once native IO has started.
            // Purge aborts that IO; drain it before releasing the shared bus to Stop/the next request.
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
        if (frame[0] != slaveAddress || frame[1] != function)
        {
            throw new InvalidDataException(
                $"ADC response address={frame[0]}, function=0x{frame[1]:X2}; " +
                $"expected address={slaveAddress}, function=0x{function:X2}.");
        }

        var expected = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(^2));
        var actual = AdcRtuFrame.CalculateCrc(frame.AsSpan(0, frame.Length - 2));
        if (actual != expected)
        {
            throw new InvalidDataException("ADC response CRC is invalid.");
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
