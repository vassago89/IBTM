using System;

namespace IBTM.Core;

// Only known failures with material still held or supported may request a finish stop.
// Motion, I/O loss and cleanup failures remain immediate faults.
public sealed class MaintenanceStopException : InvalidOperationException
{
    public MaintenanceStopException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
