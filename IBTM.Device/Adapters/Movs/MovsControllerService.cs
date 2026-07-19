using System.IO.Ports;

namespace IBTM.Device.Adapters.Movs;

/// <summary>Serial adapter for the MOVS multi-channel output controller.</summary>
public sealed class MovsControllerService : IDisposable
{
    private static readonly TimeSpan CommandDelay = TimeSpan.FromMilliseconds(50);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private SerialPort? _port;
    private string? _detectedPortName;
    private bool _disposed;

    public bool IsConnected => _port?.IsOpen == true;
    public string? PortName => _port?.PortName;

    public async Task<string?> FindAsync(CancellationToken cancellationToken = default)
    {
        foreach (var portName in SerialPort.GetPortNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Connect(portName);
                _detectedPortName = null;
                await TurnOffAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                if (_detectedPortName is not null)
                {
                    return _detectedPortName;
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                Disconnect();
            }
        }

        return null;
    }

    public void Connect(string portName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        Disconnect();

        var port = new SerialPort(portName, 19_200);
        port.DataReceived += OnDataReceived;
        try
        {
            port.Open();
            _port = port;
        }
        catch
        {
            port.DataReceived -= OnDataReceived;
            port.Dispose();
            throw;
        }
    }

    public void Disconnect()
    {
        if (_port is null)
        {
            return;
        }

        _port.DataReceived -= OnDataReceived;
        if (_port.IsOpen)
        {
            _port.Close();
        }

        _port.Dispose();
        _port = null;
    }

    public Task TurnOnAsync(CancellationToken cancellationToken = default) =>
        WriteCommandAsync('O', channel: 0, value: null, cancellationToken);

    public Task TurnOffAsync(CancellationToken cancellationToken = default) =>
        WriteCommandAsync('F', channel: 0, value: null, cancellationToken);

    public Task TurnOnAsync(int channel, CancellationToken cancellationToken = default) =>
        WriteCommandAsync('O', channel, value: null, cancellationToken);

    public Task TurnOffAsync(int channel, CancellationToken cancellationToken = default) =>
        WriteCommandAsync('F', channel, value: null, cancellationToken);

    public Task SetAsync(
        int channel,
        int value,
        CancellationToken cancellationToken = default)
    {
        if (value is < 0 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value must be between 0 and 999.");
        }

        return WriteCommandAsync('L', channel, value, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disconnect();
        _writeLock.Dispose();
    }

    private async Task WriteCommandAsync(
        char operation,
        int channel,
        int? value,
        CancellationToken cancellationToken)
    {
        ValidateChannel(channel);
        var port = _port;
        if (port?.IsOpen != true)
        {
            throw new InvalidOperationException("The MOVS serial port is not connected.");
        }

        var payload = value.HasValue
            ? $":{operation}{channel}{value.Value:000}\r\n"
            : $":{operation}{channel}\r\n";

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            port.Write(payload);
            await Task.Delay(CommandDelay, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs eventArgs) =>
        _detectedPortName = ((SerialPort)sender).PortName;

    private static void ValidateChannel(int channel)
    {
        if (channel is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel must be between 0 and 9.");
        }
    }
}
