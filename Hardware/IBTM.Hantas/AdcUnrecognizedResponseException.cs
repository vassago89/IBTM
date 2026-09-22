using System;
using System.IO;

namespace IBTM.Hantas;

// The observed CRC-valid 8C/03 reply has no confirmed ADC result or error meaning.
internal sealed class AdcUnrecognizedResponseException : IOException
{
    public AdcUnrecognizedResponseException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
