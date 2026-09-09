using System;
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
    private readonly ushort _cardNumber = GetCardNumber(settings.ControllerNumber);
    private readonly Lock _gate = new();
    private bool _initialized;

    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized) return;
            if (!Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("TMC-AE16DIOe requires a 64-bit process and tmcDApiAed_x64.dll.");

            Check(TMCAEDLL.AIO_LoadDevice(), nameof(TMCAEDLL.AIO_LoadDevice));
            try
            {
                ushort inputs = 0, outputs = 0;
                Check(TMCAEDLL.AIO_GetDiNum(_cardNumber, ref inputs), nameof(TMCAEDLL.AIO_GetDiNum));
                Check(TMCAEDLL.AIO_GetDoNum(_cardNumber, ref outputs), nameof(TMCAEDLL.AIO_GetDoNum));
                if (inputs != ChannelCount || outputs != ChannelCount)
                    throw new IOException($"AlphaMotion card={_cardNumber}: expected TMC-AE16DIOe with 16 DI / 16 DO, but found {inputs} DI / {outputs} DO.");
                _initialized = true;
                log?.Write($"AlphaMotion TMC-AE16DIOe ready: card={_cardNumber}, DI={inputs}, DO={outputs}.");
            }
            catch (Exception exception)
            {
                try { Check(TMCAEDLL.AIO_UnloadDevice(), nameof(TMCAEDLL.AIO_UnloadDevice)); }
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
        var channel = GetChannel(bit);
        lock (_gate)
        {
            EnsureReady(nameof(TMCAEDLL.AIO_GetDIBit), bit);
            ushort value = 0;
            Check(TMCAEDLL.AIO_GetDIBit(_cardNumber, channel, ref value), nameof(TMCAEDLL.AIO_GetDIBit), bit);
            return value != 0;
        }
    }

    public uint ReadInputs()
    {
        lock (_gate)
        {
            EnsureReady(nameof(TMCAEDLL.AIO_GetDIWord));
            ushort value = 0;
            // This board has exactly 16 inputs: WORD group 0 is channels 0..15.
            Check(TMCAEDLL.AIO_GetDIWord(_cardNumber, 0, ref value), nameof(TMCAEDLL.AIO_GetDIWord));
            return value;
        }
    }

    public bool ReadOutput(int bit)
    {
        var channel = GetChannel(bit);
        lock (_gate)
        {
            EnsureReady(nameof(TMCAEDLL.AIO_GetDOBit), bit);
            ushort value = 0;
            Check(TMCAEDLL.AIO_GetDOBit(_cardNumber, channel, ref value), nameof(TMCAEDLL.AIO_GetDOBit), bit);
            return value != 0;
        }
    }

    public void WriteOutput(int bit, bool value)
    {
        var channel = GetChannel(bit);
        lock (_gate)
        {
            EnsureReady(nameof(TMCAEDLL.AIO_PutDOBit), bit);
            Check(TMCAEDLL.AIO_PutDOBit(_cardNumber, channel, value ? (ushort)1 : (ushort)0),
                nameof(TMCAEDLL.AIO_PutDOBit), bit);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_initialized) return;
            _initialized = false;
            Check(TMCAEDLL.AIO_UnloadDevice(), nameof(TMCAEDLL.AIO_UnloadDevice));
        }
    }

    private void EnsureReady(string operation, int? bit = null)
    {
        if (!_initialized)
            throw new IOException($"{Address(operation, bit)} cannot run: AlphaMotion TMC-AE16DIOe is not initialized.");
    }

    private void Check(int result, string operation, int? bit = null)
    {
        // A-series APIs return 1 on success and 0 on failure, not a negative error code.
        if (result != tmcDef.TMC_ST_OK)
        {
            var error = TMCAEDLL.AIO_GetErrorCode();
            var name = typeof(tmcDef).GetFields(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(field => field.Name.StartsWith("ERR_", StringComparison.Ordinal)
                    && field.IsLiteral && field.FieldType == typeof(int)
                    && (int)field.GetRawConstantValue()! == error)?.Name ?? "UNKNOWN_ERROR";
            throw new IOException(
                $"{Address(operation, bit)} failed with AlphaMotion result {result}; {name} ({error}).");
        }
    }

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
