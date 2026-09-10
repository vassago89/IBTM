using System;
using System.Globalization;
using System.IO.Ports;
using System.Threading;

namespace IBTM.Device;

public sealed class MovsLightController(LightingSettings settings) : ILightController, IDisposable
{
    private readonly Lock _writeLock = new();
    // Connection edits apply after restart, not to an already-created driver.
    private readonly LightingSettings _connection = new()
    {
        Connection = settings.Connection,
        BaudRate = settings.BaudRate,
        DataBits = settings.DataBits,
        Parity = settings.Parity,
        StopBits = settings.StopBits,
        WriteTimeoutMilliseconds = settings.WriteTimeoutMilliseconds,
    };
    private SerialPort? _port;

    public void Initialize()
    {
        lock (_writeLock)
        {
            if (_port?.IsOpen == true)
                return;
            if (string.IsNullOrWhiteSpace(_connection.Connection))
                throw new InvalidOperationException("MOVS light COM port is empty. Set Settings > Devices & Safety > Lighting > COM Port, save and restart.");
            try
            {
                _port ??= new SerialPort(
                    _connection.Connection,
                    _connection.BaudRate,
                    _connection.Parity,
                    _connection.DataBits,
                    _connection.StopBits)
                {
                    Handshake = Handshake.None,
                    WriteTimeout = _connection.WriteTimeoutMilliseconds,
                };
                _port.Open();
            }
            catch (Exception exception) when (exception is ArgumentException
                or System.IO.IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                _port?.Dispose();
                _port = null;
                throw new InvalidOperationException(
                    $"MOVS light connection failed ({_connection.Connection}). Check Settings > Devices & Safety > Lighting. {exception.Message}",
                    exception);
            }
        }
    }

    public void SetLevel(int channel, int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 255);
        Write(channel, string.Create(CultureInfo.InvariantCulture, $":L{channel}{level:000}\r\n"));
    }

    public void TurnOn(int channel)
    {
        Write(channel, $":O{channel}\r\n");
    }

    public void TurnOff(int channel)
    {
        Write(channel, $":F{channel}\r\n");
    }

    public void TurnOffAll()
    {
        lock (_writeLock)
        {
            if (_port?.IsOpen == true)
                _port.Write(":F0\r\n");
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            try
            {
                TurnOffAll();
            }
            finally
            {
                _port?.Dispose();
                _port = null;
            }
        }
    }

    private void Write(int channel, string command)
    {
        // The protocol uses one channel digit; zero addresses all channels.
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 9);
        lock (_writeLock)
        {
            if (_port?.IsOpen != true)
                throw new InvalidOperationException("MOVS light controller is not connected.");
            _port.Write(command);
        }
    }
}
