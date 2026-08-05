using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Hantas;

public sealed class AdcClient : IDisposable
{
    private const byte ReadHoldingRegisters = 0x03;
    private const byte ReadInputRegisters = 0x04;
    private const byte WriteSingleRegister = 0x06;
    private const byte RequestDeviceInformation = 0x11;
    private const ushort ResultRegisterCount = 14;

    private readonly int _responseTimeoutMilliseconds;
    private SerialPort? _port;

    public AdcClient(int responseTimeoutMilliseconds = 1000) =>
        _responseTimeoutMilliseconds = responseTimeoutMilliseconds;

    public bool IsOpen => _port?.IsOpen == true;
    public byte SlaveAddress { get; private set; }

    public event Action<AdcFrameDirection, byte[]>? FrameTransferred;

    public static string[] GetPortNames() =>
        SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public void Open(string portName, int baudRate, byte slaveAddress)
    {
        _port = new SerialPort(
            portName,
            baudRate,
            Parity.None,
            dataBits: 8,
            StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = _responseTimeoutMilliseconds,
            WriteTimeout = _responseTimeoutMilliseconds,
        };
        _port.Open();
        SlaveAddress = slaveAddress;
    }

    public void Close()
    {
        _port?.Close();
        _port?.Dispose();
        _port = null;
    }

    public Task<ushort[]> ReadHoldingRegistersAsync(
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default) =>
        ReadRegistersAsync(
            ReadHoldingRegisters,
            address,
            count,
            cancellationToken);

    public Task<ushort[]> ReadInputRegistersAsync(
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default) =>
        ReadRegistersAsync(
            ReadInputRegisters,
            address,
            count,
            cancellationToken);

    public async Task WriteRegisterAsync(
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data, address);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), value);
        var request = BuildFrame(WriteSingleRegister, data);
        var response = await ExchangeAsync(
            WriteSingleRegister,
            request,
            cancellationToken);

        if (!request.AsSpan(0, 6).SequenceEqual(response.AsSpan(0, 6)))
        {
            throw new InvalidDataException("ADC write response does not match the request.");
        }
    }

    public async Task<byte[]> ReadDeviceInformationAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync(
            RequestDeviceInformation,
            BuildFrame(RequestDeviceInformation, []),
            cancellationToken);
        return response[3..^2];
    }

    public async Task<AdcFasteningResult> ReadFasteningResultAsync(
        CancellationToken cancellationToken = default)
    {
        var values = await ReadInputRegistersAsync(
            (ushort)AdcResultRegister.EventCount,
            ResultRegisterCount,
            cancellationToken);
        return new AdcFasteningResult(
            values[0],
            values[1],
            values[2],
            values[3] / 100.0,
            values[4] / 100.0,
            values[5],
            values[6] / 100.0,
            values[7] / 100.0,
            values[8] / 100.0,
            values[9],
            values[10],
            (AdcDirection)values[11],
            (AdcEventStatus)values[12],
            values[13]);
    }

    public Task ResetAlarmAsync(CancellationToken cancellationToken = default) =>
        WriteRegisterAsync(
            (ushort)AdcRemoteRegister.AlarmReset,
            1,
            cancellationToken);

    public Task SelectPresetAsync(
        ushort preset,
        CancellationToken cancellationToken = default) =>
        WriteRegisterAsync(
            (ushort)AdcRemoteRegister.Preset,
            preset,
            cancellationToken);

    public Task SetDirectionAsync(
        AdcDirection direction,
        CancellationToken cancellationToken = default) =>
        WriteRegisterAsync(
            (ushort)AdcRemoteRegister.Direction,
            (ushort)direction,
            cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        WriteRegisterAsync(
            (ushort)AdcRemoteRegister.RemoteStart,
            1,
            cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        WriteRegisterAsync(
            (ushort)AdcRemoteRegister.RemoteStart,
            0,
            cancellationToken);

    public void Dispose() => Close();

    private async Task<ushort[]> ReadRegistersAsync(
        byte function,
        ushort address,
        ushort count,
        CancellationToken cancellationToken)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(data, address);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), count);
        var response = await ExchangeAsync(
            function,
            BuildFrame(function, data),
            cancellationToken);
        var byteCount = response[2];
        if (byteCount != count * 2)
        {
            throw new InvalidDataException(
                $"ADC returned {byteCount} data bytes; expected {count * 2}.");
        }

        var values = new ushort[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = BinaryPrimitives.ReadUInt16BigEndian(
                response.AsSpan(3 + index * 2, 2));
        }

        return values;
    }

    private async Task<byte[]> ExchangeAsync(
        byte function,
        byte[] request,
        CancellationToken cancellationToken)
    {
        var port = _port!;
        port.DiscardInBuffer();
        FrameTransferred?.Invoke(AdcFrameDirection.Transmit, request);
        await port.BaseStream.WriteAsync(request, cancellationToken);
        await port.BaseStream.FlushAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_responseTimeoutMilliseconds);

        byte[] response;
        try
        {
            response = await ReadResponseAsync(port.BaseStream, function, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"ADC response timed out after {_responseTimeoutMilliseconds} ms.");
        }

        FrameTransferred?.Invoke(AdcFrameDirection.Receive, response);
        return response;
    }

    private async Task<byte[]> ReadResponseAsync(
        Stream stream,
        byte function,
        CancellationToken cancellationToken)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, cancellationToken);

        if ((header[1] & 0x80) != 0)
        {
            var tail = new byte[3];
            await stream.ReadExactlyAsync(tail, cancellationToken);
            var errorFrame = Join(header, tail);
            ValidateFrame(errorFrame, (byte)(function | 0x80));
            throw new AdcProtocolException((AdcExceptionCode)tail[0]);
        }

        byte[] response;
        if (function == WriteSingleRegister)
        {
            var tail = new byte[6];
            await stream.ReadExactlyAsync(tail, cancellationToken);
            response = Join(header, tail);
        }
        else
        {
            var count = new byte[1];
            await stream.ReadExactlyAsync(count, cancellationToken);
            var byteCount = count[0];
            var tail = new byte[byteCount + 2];
            await stream.ReadExactlyAsync(tail, cancellationToken);
            response = new byte[3 + tail.Length];
            header.CopyTo(response, 0);
            response[2] = byteCount;
            tail.CopyTo(response, 3);
        }

        ValidateFrame(response, function);
        return response;
    }

    private byte[] BuildFrame(byte function, ReadOnlySpan<byte> data)
    {
        var frame = new byte[data.Length + 4];
        frame[0] = SlaveAddress;
        frame[1] = function;
        data.CopyTo(frame.AsSpan(2));
        BinaryPrimitives.WriteUInt16LittleEndian(
            frame.AsSpan(frame.Length - 2),
            CalculateCrc(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }

    private void ValidateFrame(byte[] frame, byte function)
    {
        if (frame[0] != SlaveAddress || frame[1] != function)
        {
            throw new InvalidDataException("ADC response address or function is invalid.");
        }

        var expected = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(^2));
        var actual = CalculateCrc(frame.AsSpan(0, frame.Length - 2));
        if (actual != expected)
        {
            throw new InvalidDataException("ADC response CRC is invalid.");
        }
    }

    private static ushort CalculateCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 1) == 1
                    ? (crc >> 1) ^ 0xA001
                    : crc >> 1);
            }
        }

        return crc;
    }

    private static byte[] Join(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }
}
