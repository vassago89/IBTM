using System.IO;

namespace IBTM.Hantas;

public sealed class AdcProtocolException(AdcExceptionCode code)
    : IOException($"ADC controller returned {code} (0x{(byte)code:X2}).")
{
    public AdcExceptionCode Code { get; } = code;
}
