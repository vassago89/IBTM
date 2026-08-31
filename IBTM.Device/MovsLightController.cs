using System;
using System.IO.Ports;
using System.Threading;

namespace IBTM.Device;

public sealed class MovsLightController(string connection) : ILightController, IDisposable
{
    private readonly Lock _writeLock = new();
    private readonly SerialPort _port = new(connection, 19_200);

    public void Initialize()
    {
        if (!_port.IsOpen)
        {
            _port.Open();
        }
    }

    public void SetLevel(int channel, int level) =>
        Write($":L{channel}{level:000}\r\n");

    public void TurnOn(int channel) =>
        Write($":O{channel}\r\n");

    public void TurnOff(int channel) =>
        Write($":F{channel}\r\n");

    public void TurnOffAll()
    {
        if (_port.IsOpen)
        {
            Write(":F0\r\n");
        }
    }

    public void Dispose()
    {
        if (_port.IsOpen)
        {
            TurnOffAll();
        }

        _port.Dispose();
    }

    private void Write(string command)
    {
        lock (_writeLock)
        {
            _port.Write(command);
        }
    }
}
