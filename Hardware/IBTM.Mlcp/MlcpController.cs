using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace IBTM.Mlcp;

public enum MlcpCommandFormat
{
    CommandCrLf,
    CrCommandCr,
}

public enum MlcpFrameDirection
{
    Transmit,
    Receive,
}

public sealed class MlcpController : IDisposable
{
    private readonly ILogger<MlcpController>? _log;
    private readonly SemaphoreSlim _exchange;
    private readonly Lock _gate;
    private SerialPort? _port;
    private Stream? _stream;
    private CancellationTokenSource? _pending;

    public MlcpController(ILogger<MlcpController>? log = null)
    {
        _log = log;
        _exchange = new(1, 1);
        _gate = new();
        ResponseTimeout = TimeSpan.FromSeconds(3);
        CommandFormat = MlcpCommandFormat.CommandCrLf;
    }

    // Tests use a duplex stream without opening a serial port.
    internal MlcpController(Stream stream) : this()
    {
        _stream = stream;
    }

    public TimeSpan ResponseTimeout { get; set; }
    public MlcpCommandFormat CommandFormat { get; set; }
    public bool IsOpen => _port?.IsOpen ?? _stream is not null;
    public string? PortName => _port?.PortName ?? (_stream is not null ? "Virtual" : null);
    public int? BaudRate => _port?.BaudRate;

    public event Action<MlcpFrameDirection, byte[]>? FrameTransferred;

    public void Open(string portName, int baudRate = 9600)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (baudRate is < 9600 or > 115200)
            throw new ArgumentOutOfRangeException(nameof(baudRate), "LVDT supports 9600–115200 baud.");
        lock (_gate)
        {
            if (_pending is not null || _stream is not null)
                throw new InvalidOperationException("Close the LVDT connection before opening another port.");
            var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                Encoding = Encoding.ASCII,
            };
            try
            {
                port.Open();
                _stream = port.BaseStream;
                _port = port;
            }
            catch
            {
                port.Dispose();
                throw;
            }
            _log?.LogInformation("LVDT [{Port}] opened at {BaudRate} baud, 8N1.", portName, baudRate);
        }
    }

    public async Task<MlcpResult> MeasureAsync(CancellationToken cancellationToken = default)
    {
        return await RequestAsync("START", cancellationToken).ConfigureAwait(false);
    }

    public async Task<MlcpResult> ReadLastMeasurementAsync(CancellationToken cancellationToken = default)
    {
        return await RequestAsync("RECALL", cancellationToken).ConfigureAwait(false);
    }

    private async Task<MlcpResult> RequestAsync(string command, CancellationToken cancellationToken)
    {
        var timeoutDuration = ResponseTimeout;
        if (timeoutDuration <= TimeSpan.Zero || timeoutDuration.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ResponseTimeout));
        var commandText = CommandFormat switch
        {
            MlcpCommandFormat.CommandCrLf => command + "\r\n",
            MlcpCommandFormat.CrCommandCr => "\r" + command + "\r",
            _ => throw new ArgumentOutOfRangeException(nameof(CommandFormat)),
        };
        await _exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(timeoutDuration);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            Stream stream;
            lock (_gate)
            {
                _pending = pending;
                stream = _stream ?? throw new IOException("LVDT is not connected.");
                _port?.DiscardInBuffer();
            }
            var token = pending.Token;
            token.ThrowIfCancellationRequested();
            var commandBytes = Encoding.ASCII.GetBytes(commandText);
            await stream.WriteAsync(commandBytes, token).AsTask().WaitAsync(token).ConfigureAwait(false);
            _log?.LogDebug("LVDT TX {Bytes}", Convert.ToHexString(commandBytes));
            FrameTransferred?.Invoke(MlcpFrameDirection.Transmit, commandBytes);

            var received = new List<byte>();
            var buffer = new byte[256];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var length = await stream.ReadAsync(buffer, token).AsTask().WaitAsync(token).ConfigureAwait(false);
                if (length == 0)
                    throw new IOException("LVDT disconnected before returning a measurement.");
                var bytes = buffer[..length];
                _log?.LogDebug("LVDT RX {Bytes}", Convert.ToHexString(bytes));
                FrameTransferred?.Invoke(MlcpFrameDirection.Receive, bytes);
                for (var index = 0; index < length; index++)
                {
                    var value = buffer[index];
                    if (value == 0x05)
                        received.Clear();
                    else if (received.Count == 0)
                    {
                        if (value == 0x02)
                            throw new InvalidDataException("LVDT returned HEX data. Set Serial Type to ASCII.");
                        continue; // ACK is not a measurement result.
                    }
                    received.Add(value);
                    if (received.Count > 4096)
                        throw new InvalidDataException("LVDT measurement frame exceeds 4096 bytes.");
                    if (value == 0x0A)
                    {
                        var frame = received.ToArray();
                        return MlcpResult.Parse(frame);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            Close();
            throw new TimeoutException($"LVDT {command} response timed out after {timeoutDuration.TotalSeconds:0.###} s.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Close();
            throw new IOException("LVDT connection closed while waiting for a measurement.");
        }
        catch
        {
            // No transaction ID: a late result must not be reused by the next request.
            Close();
            throw;
        }
        finally
        {
            lock (_gate)
                _pending = null;
            _exchange.Release();
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            var port = _port;
            var stream = _stream;
            _port = null;
            _stream = null;
            if (port is not null)
                port.Dispose();
            else
                stream?.Dispose();
        }
    }

    public void Dispose()
    {
        Close();
    }
}
