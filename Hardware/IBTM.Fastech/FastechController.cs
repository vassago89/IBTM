using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FASTECH;
using Microsoft.Extensions.Logging;

namespace IBTM.Fastech;

public sealed class FastechController : IDisposable
{
    // The SDK assigns this connection ID locally; the drive is selected by its IP.
    public const int BoardId = 0;

    private readonly string _ipAddress;
    private readonly ILogger<FastechController>? _log;
    private bool _opened;

    public FastechController(FastechSettings settings, ILogger<FastechController>? log = null)
    {
        _ipAddress = settings.IpAddress;
        _log = log;
        SyncRoot = new();
    }

    internal Lock SyncRoot { get; }
    internal bool IsOpen => _opened;

    public void Initialize()
    {
        lock (SyncRoot)
        {
            if (!_opened)
            {
                if (!IPAddress.TryParse(_ipAddress, out var address)
                    || address.AddressFamily != AddressFamily.InterNetwork)
                    throw new ArgumentException("FASTECH requires an IPv4 address.");
                if (!Environment.Is64BitProcess)
                    throw new PlatformNotSupportedException("FASTECH requires a 64-bit process.");
                if (!EziMOTIONPlusELib.FAS_ConnectTCP(address, BoardId))
                {
                    EziMOTIONPlusELib.FAS_Close(BoardId);
                    throw new IOException($"FASTECH {_ipAddress} connection failed.");
                }
                _opened = true;
                _log?.LogInformation("FASTECH {IpAddress} connected over Ethernet TCP.", _ipAddress);
            }

            var status = 0U;
            Check(EziMOTIONPlusELib.FAS_GetAxisStatus(BoardId, ref status),
                nameof(EziMOTIONPlusELib.FAS_GetAxisStatus));
        }
    }

    internal void EnsureOpen()
    {
        if (!_opened)
            throw new IOException($"FASTECH {_ipAddress} has not been connected.");
    }

    internal void Check(int result, string operation)
    {
        if (result != EziMOTIONPlusELib.FMM_OK)
            throw new IOException($"{operation} failed: IP={_ipAddress}, result={result} (0x{result:X2}).");
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (!_opened)
                return;
            EziMOTIONPlusELib.FAS_Close(BoardId);
            _opened = false;
        }
    }
}
