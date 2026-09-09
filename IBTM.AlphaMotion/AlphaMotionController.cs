using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using IBTM.Core;
using Shared;

namespace IBTM.AlphaMotion;

public sealed class AlphaMotionController(
    AlphaMotionSettings settings,
    ApplicationLog? log = null) : IDisposable
{
    public const int ChannelCount = 16;
    private const uint PortMask = 0xFFFF;
    private readonly ushort _cardNumber = GetCardNumber(settings.ControllerNumber);
    private readonly Lock _gate = new();
    private readonly HashSet<(string Operation, int Result, int Error)> _reportedResults = [];
    private bool _initialized;

    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized) return;
            if (!Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("TMC-AE16DIOe requires a 64-bit process and tmcDApiAed_x64.dll.");

            // Manufacturer frmDIGITAL.LoadDevice checks < 0 for failure and adds 1
            // to this result for the board count. It is not a TMC_ST_OK status.
            _reportedResults.Clear();
            var loadResult = TMCAEDLL.AIO_LoadDevice();
            var loadError = TMCAEDLL.AIO_GetErrorCode();
            TraceResult(loadResult, loadError, nameof(TMCAEDLL.AIO_LoadDevice), $"loaded boards={(long)loadResult + 1}");
            if (loadResult < 0)
                throw CreateError(loadResult, loadError, nameof(TMCAEDLL.AIO_LoadDevice));
            try
            {
                // Use the same discovery API as the manufacturer's frmDIGITAL sample.
                // Invalid sentinels detect APIs that return without filling their ref parameters.
                uint model = uint.MaxValue, communication = uint.MaxValue;
                uint inputs = uint.MaxValue, outputs = uint.MaxValue;
                var result = TMCAEDLL.AIO_BoardInfo(_cardNumber, ref model, ref communication, ref inputs, ref outputs);
                var error = TMCAEDLL.AIO_GetErrorCode();
                var detail = $"model=0x{model:X}, communication=0x{communication:X}, DI={inputs}, DO={outputs}";
                TraceResult(result, error, nameof(TMCAEDLL.AIO_BoardInfo), detail);
                CheckStatus(result, error, nameof(TMCAEDLL.AIO_BoardInfo), detail);
                if (model != tmcDef.TMC_AE || communication == uint.MaxValue
                    || inputs != ChannelCount || outputs != ChannelCount)
                    throw CreateError(result, error, nameof(TMCAEDLL.AIO_BoardInfo),
                        $"Invalid or unchanged board information: {detail}. Expected model=0xAE with 16 DI / 16 DO; initialization remains blocked.");

                // Read both ports before allowing any output command or reporting readiness.
                var initialInputs = ReadPort(input: true);
                var initialOutputs = ReadPort(input: false);
                log?.Write($"AlphaMotion TMC-AE16DIOe ready: card={_cardNumber}, DI={inputs}, DO={outputs}, loaded boards={(long)loadResult + 1} (AIO_LoadDevice={loadResult}); initial DI=0x{initialInputs:X8}, DO=0x{initialOutputs:X8}.");
                _initialized = true;
            }
            catch (Exception exception)
            {
                try { Unload(); }
                catch (Exception cleanupError)
                {
                    exception.Data["AlphaMotionUnloadError"] = cleanupError.ToString();
                    log?.Error("AlphaMotion cleanup after initialization failure also failed.", cleanupError);
                }
                throw;
            }
        }
    }

    public bool ReadInput(int bit)
    {
        _ = GetChannel(bit);
        lock (_gate)
        {
            EnsureReady(nameof(TMCAEDLL.AIO_GetDIDWord), bit);
            return ((ReadPort(input: true, bit) >> bit) & 1) != 0;
        }
    }

    public uint ReadInputs()
    {
        lock (_gate)
        {
            EnsureReady(nameof(TMCAEDLL.AIO_GetDIDWord));
            return ReadPort(input: true);
        }
    }

    public bool ReadOutput(int bit)
    {
        _ = GetChannel(bit);
        lock (_gate)
        {
            EnsureReady(nameof(TMCAEDLL.AIO_GetDODWord), bit);
            return ((ReadPort(input: false, bit) >> bit) & 1) != 0;
        }
    }

    public void WriteOutput(int bit, bool value)
    {
        var channel = GetChannel(bit);
        lock (_gate)
        {
            EnsureReady(nameof(TMCAEDLL.AIO_PutDOBit), bit);
            var result = TMCAEDLL.AIO_PutDOBit(_cardNumber, channel, value ? (ushort)1 : (ushort)0);
            var error = TMCAEDLL.AIO_GetErrorCode();
            var detail = $"requested={(value ? "ON" : "OFF")}";
            TraceResult(result, error, nameof(TMCAEDLL.AIO_PutDOBit), detail, bit);
            CheckStatus(result, error, nameof(TMCAEDLL.AIO_PutDOBit), detail, bit);
            var readback = ReadPort(input: false, bit);
            if (((readback >> bit) & 1) != (value ? 1U : 0U))
                throw CreateError(result, error, nameof(TMCAEDLL.AIO_PutDOBit),
                    $"Output readback mismatch: {detail}, DO=0x{readback:X8}.", bit);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_initialized) return;
            _initialized = false;
            Unload();
        }
    }

    private void EnsureReady(string operation, int? bit = null)
    {
        if (!_initialized)
            throw new IOException($"{Address(operation, bit)} cannot run: AlphaMotion TMC-AE16DIOe is not initialized.");
    }

    private uint ReadPort(bool input, int? bit = null)
    {
        var operation = input ? nameof(TMCAEDLL.AIO_GetDIDWord) : nameof(TMCAEDLL.AIO_GetDODWord);
        uint value = uint.MaxValue;
        // The manufacturer's 16-channel UI reads DWORD group 0, then displays bits 0..15.
        var result = input
            ? TMCAEDLL.AIO_GetDIDWord(_cardNumber, 0, ref value)
            : TMCAEDLL.AIO_GetDODWord(_cardNumber, 0, ref value);
        var error = TMCAEDLL.AIO_GetErrorCode();
        var detail = $"group=0, {(input ? "DI" : "DO")}=0x{value:X8}";
        TraceResult(result, error, operation, detail, bit);
        CheckStatus(result, error, operation, detail, bit);
        if ((value & ~PortMask) != 0)
            throw CreateError(result, error, operation,
                $"Invalid or unchanged port data: {detail}. Expected a 16-bit value; it will not be reported as OFF.", bit);
        return value;
    }

    private void Unload()
    {
        var result = TMCAEDLL.AIO_UnloadDevice();
        var error = TMCAEDLL.AIO_GetErrorCode();
        TraceResult(result, error, nameof(TMCAEDLL.AIO_UnloadDevice), "controller unavailable", always: true);
        CheckStatus(result, error, nameof(TMCAEDLL.AIO_UnloadDevice));
    }

    private void CheckStatus(int result, int error, string operation, string? detail = null, int? bit = null)
    {
        // Field-test compatibility, NOT a confirmed SDK-wide return convention:
        // permit 0/ERR_SUCCESS only alongside the caller's data validation/readback.
        // Negative/unknown results or any reported SDK error always fail closed.
        if (result is not (0 or tmcDef.TMC_ST_OK) || error != tmcDef.ERR_SUCCESS)
            throw CreateError(result, error, operation, detail, bit);
    }

    private void TraceResult(int result, int error, string operation, string detail, int? bit = null, bool always = false)
    {
        // First result (and status changes) only: no per-poll log flood.
        if (always || _reportedResults.Add((operation, result, error)))
            log?.Write($"AlphaMotion native {Address(operation, bit)}: result={result}, {ErrorName(error)} ({error}); {detail}."
                + (result == 0 && operation != nameof(TMCAEDLL.AIO_LoadDevice)
                    ? " Zero-result compatibility path; hardware verification required." : ""));
    }

    private IOException CreateError(int result, int error, string operation, string? detail = null, int? bit = null) =>
        new($"{Address(operation, bit)} failed with AlphaMotion result {result}; {ErrorName(error)} ({error})."
            + (detail is null ? "" : $" {detail}"));

    private static string ErrorName(int error) =>
        typeof(tmcDef).GetFields(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(field => field.Name.StartsWith("ERR_", StringComparison.Ordinal)
                && field.IsLiteral && field.FieldType == typeof(int)
                && (int)field.GetRawConstantValue()! == error)?.Name ?? "UNKNOWN_ERROR";

    private string Address(string operation, int? bit) =>
        $"{operation} (card={_cardNumber}{(bit is null ? "" : $", bit={bit}")})";

    private static ushort GetCardNumber(int value)
    {
        if (value < 0 || value > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(AlphaMotionSettings.ControllerNumber), value,
                "AlphaMotion card number must fit the manufacturer's unsigned 16-bit address.");
        return (ushort)value;
    }

    private static ushort GetChannel(int bit)
    {
        if ((uint)bit >= ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(bit), bit, "TMC-AE16DIOe channels are 0 through 15.");
        return (ushort)bit;
    }
}
