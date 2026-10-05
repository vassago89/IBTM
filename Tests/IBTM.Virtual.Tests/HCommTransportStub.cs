using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using HComm.Common;
using HComm.Device;
using HantasComm = HComm.HComm;

namespace IBTM.Virtual.Tests;

// Run the NuGet queue and serial decoder against memory, never a physical COM port.
internal sealed class HCommTransportStub : IHComm, IDisposable
{
    private readonly HcSerial _serial;
    private readonly Timer _timer;
    private readonly Channel<byte[]> _requests;

    public HCommTransportStub(byte slaveAddress = 0)
    {
        _serial = new();
        typeof(HcSerial).GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_serial, slaveAddress);
        _timer = new Timer(IgnoreTimer);
        _requests = Channel.CreateUnbounded<byte[]>();
        Communication = new();
        typeof(HantasComm).GetProperty("Comm", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Communication, this);
        typeof(HcSerial).GetProperty("ProcessTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_serial, _timer);
        ConnectionChanged = (ChangedConnection)Delegate.CreateDelegate(typeof(ChangedConnection), Communication,
            typeof(HantasComm).GetMethod("ConnectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)!);
        Communication.Connect("Memory", 115200, slaveAddress);
    }

    public HantasComm Communication { get; }
    public bool IsConnected { get; private set; }
    public AckData AckReceived { private get; set; } = null!;
    public AckRawData AckRawReceived { private get; set; } = null!;
    public AckMorData AckMorReceived { private get; set; } = null!;
    public ChangedConnection ConnectionChanged { get; set; }

    private static void IgnoreTimer(object? state)
    {
    }

    public bool Connect(string target, int option, byte id = 1)
    {
        return true;
    }

    public bool Close()
    {
        IsConnected = false;
        ConnectionChanged(false);
        return true;
    }

    public bool Write(byte[] packet, int length)
    {
        _requests.Writer.TryWrite(packet);
        return true;
    }

    public async Task<byte[]> NextRequestAsync()
    {
        return await _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    public void Receive(byte[] raw, bool rawBeforeConnection = false)
    {
        if (rawBeforeConnection)
            AckRawReceived?.Invoke(raw);
        if (!IsConnected)
        {
            IsConnected = true;
            ConnectionChanged(true);
        }
        if (!rawBeforeConnection)
            AckRawReceived?.Invoke(raw);
        _serial.AckReceived = AckReceived;
        var queue = (ConcurrentQueue<byte>)typeof(HcSerial)
            .GetProperty("ReceiveBuf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_serial)!;
        foreach (var value in raw)
            queue.Enqueue(value);
        typeof(HcSerial).GetMethod("ProcessTimerCallback", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_serial, [this]);
    }

    public IEnumerable<byte> PacketGetParam(ushort addr, ushort count)
    {
        return _serial.PacketGetParam(addr, count);
    }

    public IEnumerable<byte> PacketSetParam(ushort addr, ushort value)
    {
        return _serial.PacketSetParam(addr, value);
    }

    public IEnumerable<byte> PacketGetState(ushort addr, ushort count)
    {
        return _serial.PacketGetState(addr, count);
    }

    public IEnumerable<byte> PacketGetInfo()
    {
        return _serial.PacketGetInfo();
    }

    public IEnumerable<byte> PacketGetGraph(ushort addr, ushort count)
    {
        return _serial.PacketGetGraph(addr, count);
    }

    public void Dispose()
    {
        if (Communication.State is ConnectionState.Connecting or ConnectionState.Connected)
            Communication.Close();
        _timer.Dispose();
    }
}
