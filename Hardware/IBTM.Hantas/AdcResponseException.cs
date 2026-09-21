using System.IO;

namespace IBTM.Hantas;

// A validated controller rejection, distinct from a broken or missing serial response.
public sealed class AdcResponseException : IOException
{
    public AdcResponseException(byte errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public byte ErrorCode { get; }
}
