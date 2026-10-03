// Source: C:/git/AnyWave/AnyWave.Device/Lights/MOVSService.cs. Private names follow this project's style;
// Connect reuses the current open port. Disconnected writes fail; disconnect always attempts port disposal.
// Unused port discovery and all-channel ON are omitted.
// Manufacturer command bytes and timing for the used operations are unchanged.
using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using IBTM.Core;

namespace AnyWave.Device.LightControllers;

public class MOVSService
{
    private SerialPort? _port;

    public void Connect(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            return;

        if (_port?.IsOpen == true && string.Equals(_port.PortName, portName, StringComparison.OrdinalIgnoreCase))
            return;

        _port?.Dispose();
        _port = new SerialPort(portName, 19200);
        _port.Open();
    }

    public void Disconnect()
    {
        Exception? failure = null;
        try
        {
            if (_port?.IsOpen == true)
                Off();
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                _port?.Dispose();
                _port = null;
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
        }
    }

    public void Off()
    {
        if (_port is not { IsOpen: true })
            throw new IOException(UiText.Get("Light disconnected · check COM port"));

        char[] buffer =
        [
            ':',
            'F',
            '0',
            '\r',
            '\n'
        ];

        _port.Write(buffer, 0, buffer.Length);
        Thread.Sleep(50);
    }

    public void On(int channel)
    {
        if (_port is not { IsOpen: true })
            throw new IOException(UiText.Get("Light disconnected · check COM port"));

        char[] buffer =
        [
            ':',
            'O',
            channel.ToString()[0],
            '\r',
            '\n'
        ];

        _port.Write(buffer, 0, buffer.Length);
        Thread.Sleep(50);
    }

    public void Off(int channel)
    {
        if (_port is not { IsOpen: true })
            throw new IOException(UiText.Get("Light disconnected · check COM port"));

        char[] buffer =
        [
            ':',
            'F',
            channel.ToString()[0],
            '\r',
            '\n'
        ];

        _port.Write(buffer, 0, buffer.Length);
        Thread.Sleep(50);
    }

    public void Set(int channel, int value)
    {
        if (_port is not { IsOpen: true })
            throw new IOException(UiText.Get("Light disconnected · check COM port"));

        var @string = value.ToString("000");

        char[] buffer =
        [
            ':',
            'L',
            channel.ToString()[0],
            @string[0],
            @string[1],
            @string[2],
            '\r',
            '\n'
        ];

        _port.Write(buffer, 0, buffer.Length);
        Thread.Sleep(50);
    }
}
