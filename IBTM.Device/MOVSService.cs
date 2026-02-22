using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IBTM.Device
{
    public class MOVSService
    {
        private SerialPort _port;
        private string portName;

        public string Find()
        {
            foreach (var port in SerialPort.GetPortNames())
            {
                Connect(port);
                _port.DataReceived += _port_DataReceived;
                
                Off();
            }

            Thread.Sleep(1000);

            return portName;
        }

        private void _port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            portName = ((SerialPort)sender).PortName;
        }

        public void Connect(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName))
                return;

            _port?.Close();
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
