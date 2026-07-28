using System;

namespace IBTM.Ajin;

public sealed class AjinController(AjinSettings settings) : IDisposable
{
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
        Check(
            AjinNative.AxmMotLoadParaAll(settings.MotionParameterFile),
            nameof(AjinNative.AxmMotLoadParaAll));
        _initialized = true;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            AjinNative.AxlClose();
            _initialized = false;
        }
    }

    internal static void Check(uint result, string operation)
    {
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"{operation} failed with Ajin result 0x{result:X8}.");
        }
    }
}
