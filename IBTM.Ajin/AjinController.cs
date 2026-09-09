using System;
using System.IO;

namespace IBTM.Ajin;

public sealed class AjinController(AjinSettings settings) : IDisposable
{
    private const int RtexChannelCountPerModule = 32;
    private bool _initialized;

    public int RtexInputWordCount => settings.RtexInputModules.Length;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        Check(
            CAXL.AxlOpen(settings.InterruptNumber),
            nameof(CAXL.AxlOpen));
        try
        {
            Check(
                CAXM.AxmMotLoadParaAll(Path.Combine(
                    AppContext.BaseDirectory,
                    settings.MotionParameterFile)),
                nameof(CAXM.AxmMotLoadParaAll));
            _initialized = true;
        }
        catch
        {
            CAXL.AxlClose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_initialized)
        {
            CAXL.AxlClose();
            _initialized = false;
        }
    }

    public bool ReadRtexInput(int channel)
    {
        var (module, offset) = GetRtexAddress(
            settings.RtexInputModules,
            channel);
        var value = 0U;
        Check(
            CAXD.AxdiReadInportBit(
                module,
                offset,
                ref value),
            nameof(CAXD.AxdiReadInportBit));
        return value != 0;
    }

    public void ReadRtexInputs(uint[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            var value = 0U;
            Check(
                CAXD.AxdiReadInportDword(
                    settings.RtexInputModules[index],
                    0,
                    ref value),
                nameof(CAXD.AxdiReadInportDword));
            values[index] = value;
        }
    }

    public bool ReadRtexOutput(int channel)
    {
        var (module, offset) = GetRtexAddress(
            settings.RtexOutputModules,
            channel);
        var value = 0U;
        Check(
            CAXD.AxdoReadOutportBit(
                module,
                offset,
                ref value),
            nameof(CAXD.AxdoReadOutportBit));
        return value != 0;
    }

    public void WriteRtexOutput(int channel, bool value)
    {
        var (module, offset) = GetRtexAddress(
            settings.RtexOutputModules,
            channel);
        Check(
            CAXD.AxdoWriteOutportBit(
                module,
                offset,
                value ? 1U : 0U),
            nameof(CAXD.AxdoWriteOutportBit));
    }

    private static (int Module, int Offset) GetRtexAddress(
        int[] modules,
        int channel) =>
        (modules[channel / RtexChannelCountPerModule],
            channel % RtexChannelCountPerModule);

    internal static void Check(uint result, string operation)
    {
        if (result != (uint)AXT_FUNC_RESULT.AXT_RT_SUCCESS)
        {
            throw new IOException(
                $"{operation} failed with Ajin result {(AXT_FUNC_RESULT)result} (0x{result:X8}).");
        }
    }
}
