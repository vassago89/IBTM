using System;
using System.IO;

namespace IBTM.Ajin;

public sealed class AjinController(AjinSettings settings) : IDisposable
{
    private const int RtexChannelCountPerModule = 32;
    private static readonly int[] RtexInputModules = [0, 1, 4];
    private static readonly int[] RtexOutputModules = [2, 3, 4];
    private bool _initialized;

    internal AjinSettings Settings => settings;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        Check(
            AjinNative.AxlOpen(settings.InterruptNumber),
            nameof(AjinNative.AxlOpen));
        _initialized = true;
        Check(
            AjinNative.AxmMotLoadParaAll(Path.Combine(
                AppContext.BaseDirectory,
                settings.MotionParameterFile)),
            nameof(AjinNative.AxmMotLoadParaAll));
    }

    public void Dispose()
    {
        if (_initialized)
        {
            AjinNative.AxlClose();
            _initialized = false;
        }
    }

    public bool ReadRtexInput(int channel)
    {
        var (module, offset) = GetRtexAddress(
            RtexInputModules,
            channel);
        var value = 0U;
        Check(
            AjinNative.AxdiReadInportBit(
                module,
                offset,
                ref value),
            nameof(AjinNative.AxdiReadInportBit));
        return value != 0;
    }

    public bool ReadRtexOutput(int channel)
    {
        var (module, offset) = GetRtexAddress(
            RtexOutputModules,
            channel);
        var value = 0U;
        Check(
            AjinNative.AxdoReadOutportBit(
                module,
                offset,
                ref value),
            nameof(AjinNative.AxdoReadOutportBit));
        return value != 0;
    }

    public void WriteRtexOutput(int channel, bool value)
    {
        var (module, offset) = GetRtexAddress(
            RtexOutputModules,
            channel);
        Check(
            AjinNative.AxdoWriteOutportBit(
                module,
                offset,
                value ? 1U : 0U),
            nameof(AjinNative.AxdoWriteOutportBit));
    }

    private static (int Module, int Offset) GetRtexAddress(
        int[] modules,
        int channel) =>
        (modules[channel / RtexChannelCountPerModule],
            channel % RtexChannelCountPerModule);

    internal static void Check(uint result, string operation)
    {
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"{operation} failed with Ajin result 0x{result:X8}.");
        }
    }
}
