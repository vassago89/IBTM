// Source: C:/git/AnyWave/AnyWave.Device/Lights/MOVSService.cs. Private names follow this project's style;
// Connect reuses the current open port. Manufacturer command bytes and timing are unchanged.
#nullable disable
using System.Threading;

using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnyWave.Device.LightControllers
{
    public class MOVSService
    {
        private SerialPort _port;
        private string _portName;

        public string Find()
        {
            foreach (var port in SerialPort.GetPortNames())
            {
                Connect(port);
                _port.DataReceived += OnPortDataReceived;
                
                Off();
            }

            Thread.Sleep(1000);

            return _portName;
        }

        private void OnPortDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            _portName = ((SerialPort)sender).PortName;
        }

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
            if (_port != null && _port.IsOpen)
                Off();

            _port?.Close();
        }

        public void On()
        {
            if (_port?.IsOpen == false)
                return;

            IEnumerable<char> buffer =
            [
                ':',
                'O',
                '0',
                '\r',
                '\n'
            ];

            _port?.Write(buffer.ToArray(), 0, buffer.Count());
            Thread.Sleep(50);
        }

        public void Off()
        {
            if (_port?.IsOpen == false)
                return;

            IEnumerable<char> buffer =
            [
                ':',
                'F',
                '0',
                '\r',
                '\n'
            ];

            _port?.Write(buffer.ToArray(), 0, buffer.Count());
            Thread.Sleep(50);
        }

        public void On(int channel)
        {
            if (_port?.IsOpen == false)
                return;

            IEnumerable<char> buffer =
            [
                ':',
                'O',
                channel.ToString()[0],
                '\r',
                '\n'
            ];

            _port?.Write(buffer.ToArray(), 0, buffer.Count());
            Thread.Sleep(50);
        }

        public void Off(int channel)
        {
            if (_port?.IsOpen == false)
                return;

            IEnumerable<char> buffer =
            [
                ':',
                'F',
                channel.ToString()[0],
                '\r',
                '\n'
            ];

            _port?.Write(buffer.ToArray(), 0, buffer.Count());
            Thread.Sleep(50);
        }

        public void Set(int channel, int value)
        {
            if (_port?.IsOpen == false)
                return;

            var @string = value.ToString("000");

            IEnumerable<char> buffer =
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

            _port?.Write(buffer.ToArray(), 0, buffer.Count());
            Thread.Sleep(50);
        }
    }
}
