using System;
using System.IO;

namespace IBTM.Hantas;

// HComm decoded a reply that cannot be assigned to the pending request.
internal sealed class AdcUnexpectedResponseException : IOException
{
    public AdcUnexpectedResponseException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
