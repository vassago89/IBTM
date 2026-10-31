using System;
using System.IO;
using System.Threading;
using FASTECH;
using Microsoft.Extensions.Logging;

namespace IBTM.Fastech;

public sealed class FastechController : IDisposable
{
    private readonly uint _baudRate;
    private readonly ILogger<FastechController>? _log;
    private bool _opened;

    public FastechController(FastechSettings settings, ILogger<FastechController>? log = null)
    {
        if (!settings.PortName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || !byte.TryParse(settings.PortName.AsSpan(3), out var port) || port == 0)
            throw new ArgumentException("FASTECH requires a COM port between COM1 and COM255.");
        if (settings.BaudRate is not (9600 or 19200 or 38400 or 57600 or 115200))
            throw new ArgumentOutOfRangeException(nameof(settings), "Unsupported FASTECH baud rate.");
        PortNumber = port;
        _baudRate = settings.BaudRate;
        _log = log;
        SyncRoot = new();
    }

    internal byte PortNumber { get; }
    internal Lock SyncRoot { get; }

    public void Initialize(byte slaveNumber)
    {
        if (slaveNumber >= EziMOTIONPlusRLib.MAX_SLAVE_NUMS)
            throw new ArgumentOutOfRangeException(nameof(slaveNumber));
        lock (SyncRoot)
        {
            if (!_opened)
            {
                if (!Environment.Is64BitProcess)
                    throw new PlatformNotSupportedException("FASTECH requires a 64-bit process.");
                if (EziMOTIONPlusRLib.FAS_Connect(PortNumber, _baudRate) == 0)
                {
                    EziMOTIONPlusRLib.FAS_Close(PortNumber);
                    throw new IOException($"FASTECH COM{PortNumber} connection failed.");
                }
                _opened = true;
                _log?.LogInformation("FASTECH COM{Port} connected at {BaudRate} baud.", PortNumber, _baudRate);
            }

            // An open port is not proof that this slave is responding.
            var status = 0U;
            Check(EziMOTIONPlusRLib.FAS_GetAxisStatus(PortNumber, slaveNumber, ref status),
                nameof(EziMOTIONPlusRLib.FAS_GetAxisStatus), slaveNumber);
        }
    }

    internal void EnsureOpen()
    {
        if (!_opened)
            throw new IOException($"FASTECH COM{PortNumber} has not been opened.");
    }

    internal void Check(int result, string operation, byte slaveNumber)
    {
        if (result != EziMOTIONPlusRLib.FMM_OK)
            throw new IOException($"{operation} failed: COM{PortNumber}, slave={slaveNumber}, result={result} (0x{result:X2}).");
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (!_opened)
                return;
            EziMOTIONPlusRLib.FAS_Close(PortNumber);
            _opened = false;
        }
    }
}
