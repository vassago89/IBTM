using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using IBTM.Core;
using Shared;

namespace IBTM.AlphaMotion;

public sealed class AlphaMotionController(AlphaMotionSettings settings, ApplicationLog? log = null) : IDisposable
{
    // Reserved logical address window. AJIN starts at 16; this is not a board-size requirement.
    public const int ChannelCount = 16;
    private const uint MappedPortMask = (1U << ChannelCount) - 1;
    private readonly ushort _cardNumber = GetCardNumber(settings.ControllerNumber);
    private readonly Lock _gate = new();
    private readonly HashSet<(string Operation, int Result, int Error)> _reportedResults = [];
    private bool _initialized;
    private uint _inputCount;
    private uint _outputCount;

    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                try
                {
                    _ = ReadPort(input: true);
                    _ = ReadPort(input: false);
                    return;
                }
                catch (IOException exception)
                {
                    log?.Error("AlphaMotion connection probe failed; reloading the device.", exception);
                    Dispose();
                }
            }
            if (!Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("TMC-AE16DIOe requires a 64-bit process and tmcDApiAed_x64.dll.");
            // Manufacturer frmDIGITAL.LoadDevice checks < 0 for failure and adds 1
            // to this result for the board count. It is not a TMC_ST_OK status.
            _reportedResults.Clear();
            var loadResult = TMCAEDLL.AIO_LoadDevice();
            var loadError = TMCAEDLL.AIO_GetErrorCode();
            TraceResult(
                loadResult,
                loadError,
                nameof(TMCAEDLL.AIO_LoadDevice),
                $"loaded boards={(long)loadResult + 1}");
            if (loadResult < 0)
                throw CreateError(loadResult, loadError, nameof(TMCAEDLL.AIO_LoadDevice));
            try
            {
                // Use the same discovery API as the manufacturer's frmDIGITAL sample.
                // Invalid sentinels detect APIs that return without filling their ref parameters.
                uint model = uint.MaxValue, communication = uint.MaxValue;
                uint inputs = uint.MaxValue, outputs = uint.MaxValue;
                var result = TMCAEDLL.AIO_BoardInfo(
                    _cardNumber,
                    ref model,
                    ref communication,
                    ref inputs,
                    ref outputs);
                var error = TMCAEDLL.AIO_GetErrorCode();
                var detail = $"model=0x{model:X}, communication=0x{communication:X}, DI={inputs}, DO={outputs}";
                TraceResult(result, error, nameof(TMCAEDLL.AIO_BoardInfo), detail);
                CheckStatus(result, error, nameof(TMCAEDLL.AIO_BoardInfo), detail);
                // Identity codes and exact board size do not determine compatibility.
                // Retain the actual counts to reject only unavailable channel addresses.
                if (inputs > (uint)ushort.MaxValue + 1
                    || outputs > (uint)ushort.MaxValue + 1
                    || (inputs == 0 && outputs == 0))
                    throw CreateError(
                        result,
                        error,
                        nameof(TMCAEDLL.AIO_BoardInfo),
                        $"Invalid or unchanged I/O counts: {detail}. Counts must fit the SDK channel address range and expose at least one I/O channel.");
                _inputCount = inputs;
                _outputCount = outputs;
                // Probe each available direction before allowing any output command.
                var initialInputs = ReadPort(input: true);
                var initialOutputs = ReadPort(input: false);
                log?.Write(
                    $"AlphaMotion ready: card={_cardNumber}, DI={inputs}, DO={outputs}, loaded boards={(long)loadResult + 1} (AIO_LoadDevice={loadResult}); initial DI=0x{initialInputs:X8}, DO=0x{initialOutputs:X8}.");
                _initialized = true;
            }
            catch (Exception exception)
            {
                try
                {
                    Unload();
                }
                catch (Exception cleanupError)
                {
                    exception.Data["AlphaMotionUnloadError"] = cleanupError.ToString();
                    log?.Error(
                        "AlphaMotion cleanup after initialization failure also failed.",
                        cleanupError);
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
            EnsureChannelAvailable(bit, input: true);
            return ((ReadPort(input: true, bit) >> bit) & 1) != 0;
        }
    }

    public uint ReadInputs()
    {
        lock (_gate)
        {
            EnsureReady(nameof(TMCAEDLL.AIO_GetDIDWord));
            return ReadPort(input: true) & MappedPortMask;
        }
    }

    public bool ReadOutput(int bit)
    {
        _ = GetChannel(bit);
        lock (_gate)
        {
            EnsureReady(nameof(TMCAEDLL.AIO_GetDODWord), bit);
            EnsureChannelAvailable(bit, input: false);
            return ((ReadPort(input: false, bit) >> bit) & 1) != 0;
        }
    }

    public void WriteOutput(int bit, bool value)
    {
        var channel = GetChannel(bit);
        lock (_gate)
        {
            EnsureReady(nameof(TMCAEDLL.AIO_PutDOBit), bit);
            EnsureChannelAvailable(bit, input: false);
            var result = TMCAEDLL.AIO_PutDOBit(_cardNumber, channel, value ? (ushort)1 : (ushort)0);
            var error = TMCAEDLL.AIO_GetErrorCode();
            var detail = $"requested={(value ? "ON" : "OFF")}";
            TraceResult(result, error, nameof(TMCAEDLL.AIO_PutDOBit), detail, bit);
            CheckStatus(result, error, nameof(TMCAEDLL.AIO_PutDOBit), detail, bit);
            var readback = ReadPort(input: false, bit);
            if (((readback >> bit) & 1) != (value ? 1U : 0U))
                throw CreateError(
                    result,
                    error,
                    nameof(TMCAEDLL.AIO_PutDOBit),
                    $"Output readback mismatch: {detail}, DO=0x{readback:X8}.",
                    bit);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_initialized)
                return;
            _initialized = false;
            Unload();
        }
    }

    private void EnsureReady(string operation, int? bit = null)
    {
        if (!_initialized)
            throw new IOException(
                $"{Address(operation, bit)} cannot run: AlphaMotion TMC-AE16DIOe is not initialized.");
    }

    private uint ReadPort(bool input, int? bit = null)
    {
        var count = input ? _inputCount : _outputCount;
        if (count == 0)
            return 0; // This direction has no channels; individual access is rejected.
        var operation = input ? nameof(TMCAEDLL.AIO_GetDIDWord) : nameof(TMCAEDLL.AIO_GetDODWord);
        var mask = count >= 32 ? uint.MaxValue : (1U << (int)count) - 1;
        var read = ReadPortValue(input, uint.MaxValue, bit);
        if (mask == uint.MaxValue && read.Value == uint.MaxValue)
        {
            // On wider boards all 32 bits ON is valid. Change the seed to distinguish
            // real data from an untouched ref buffer; allow an ON -> OFF transition.
            read = ReadPortValue(input, 0, bit);
            if (read.Value == 0)
            {
                read = ReadPortValue(input, uint.MaxValue, bit);
                if (read.Value == uint.MaxValue)
                    throw CreateError(
                        read.Result,
                        read.Error,
                        operation,
                        "Invalid or unchanged port data after changing the read-buffer seed; I/O state is unavailable.",
                        bit);
            }
        }

        if ((read.Value & ~mask) != 0)
            throw CreateError(
                read.Result,
                read.Error,
                operation,
                $"Invalid or unchanged port data: group=0, {(input ? "DI" : "DO")}=0x{read.Value:X8}. Reported channel count={count}; it will not be reported as OFF.",
                bit);
        return read.Value;
    }

    private (uint Value, int Result, int Error) ReadPortValue(bool input, uint initialValue, int? bit)
    {
        var operation = input ? nameof(TMCAEDLL.AIO_GetDIDWord) : nameof(TMCAEDLL.AIO_GetDODWord);
        var value = initialValue;
        // Keep the manufacturer's DWORD group-0 read; do not shift the application's mapping.
        var result = input
            ? TMCAEDLL.AIO_GetDIDWord(_cardNumber, 0, ref value)
            : TMCAEDLL.AIO_GetDODWord(_cardNumber, 0, ref value);
        var error = TMCAEDLL.AIO_GetErrorCode();
        var detail = $"group=0, {(input ? "DI" : "DO")}=0x{value:X8}";
        TraceResult(result, error, operation, detail, bit);
        CheckStatus(result, error, operation, detail, bit);
        return (value, result, error);
    }

    private void EnsureChannelAvailable(int bit, bool input)
    {
        var count = input ? _inputCount : _outputCount;
        if ((uint)bit >= count)
            throw new ArgumentOutOfRangeException(
                nameof(bit),
                bit,
                $"AlphaMotion card={_cardNumber} reports {count} {(input ? "DI" : "DO")} channels; this mapped channel does not exist.");
    }

    private void Unload()
    {
        var result = TMCAEDLL.AIO_UnloadDevice();
        var error = TMCAEDLL.AIO_GetErrorCode();
        TraceResult(
            result,
            error,
            nameof(TMCAEDLL.AIO_UnloadDevice),
            "controller unavailable",
            always: true);
        CheckStatus(result, error, nameof(TMCAEDLL.AIO_UnloadDevice));
    }

    private void CheckStatus(
        int result,
        int error,
        string operation,
        string? detail = null,
        int? bit = null)
    {
        // Field-test compatibility, NOT a confirmed SDK-wide return convention:
        // permit 0/ERR_SUCCESS only alongside the caller's data validation/readback.
        // Negative/unknown results or any reported SDK error always fail closed.
        if (result is not (0 or tmcDef.TMC_ST_OK) || error != tmcDef.ERR_SUCCESS)
            throw CreateError(result, error, operation, detail, bit);
    }

    private void TraceResult(
        int result,
        int error,
        string operation,
        string detail,
        int? bit = null,
        bool always = false)
    {
        // First result (and status changes) only: no per-poll log flood.
        if (always || _reportedResults.Add((operation, result, error)))
            log?.Write(
                $"AlphaMotion native {Address(operation, bit)}: result={result}, {ErrorName(error)} ({error}); {detail}." + (result == 0 && operation != nameof(
                    TMCAEDLL.AIO_LoadDevice)
                    ? " Zero-result compatibility path; hardware verification required."
                    : ""));
    }

    private IOException CreateError(
        int result,
        int error,
        string operation,
        string? detail = null,
        int? bit = null)
    {
        return new(
            $"{Address(operation, bit)} failed with AlphaMotion result {result}; {ErrorName(error)} ({error})." + (detail is null ? "" : $" {detail}"));
    }

    private static string ErrorName(int error)
    {
        return typeof(tmcDef).GetFields(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(
                field =>
                    field.Name.StartsWith("ERR_", StringComparison.Ordinal)
                        && field.IsLiteral
                        && field.FieldType == typeof(int)
                        && (int)field.GetRawConstantValue()! == error)?.Name ?? "UNKNOWN_ERROR";
    }

    private string Address(string operation, int? bit)
    {
        return $"{operation} (card={_cardNumber}{(bit is null ? "" : $", bit={bit}")})";
    }

    private static ushort GetCardNumber(int value)
    {
        if (value < 0 || value > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(AlphaMotionSettings.ControllerNumber),
                value,
                "AlphaMotion card number must fit the manufacturer's unsigned 16-bit address.");
        return (ushort)value;
    }

    private static ushort GetChannel(int bit)
    {
        if ((uint)bit >= ChannelCount)
            throw new ArgumentOutOfRangeException(
                nameof(bit),
                bit,
                "AlphaMotion mapped channels are 0 through 15; logical addresses 16 and above belong to AJIN.");
        return (ushort)bit;
    }
}
