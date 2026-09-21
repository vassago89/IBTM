using System;
using System.IO;

namespace IBTM.Hantas;

// A validated controller rejection, distinct from a broken or missing serial response.
public sealed class AdcResponseException : IOException
{
    public AdcResponseException(byte errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public byte ErrorCode { get; }
}
