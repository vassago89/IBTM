using System;

namespace IBTM.Device;

// Expected refusal from current equipment feedback; keep programming errors distinct.
public sealed class MotionInterlockException : InvalidOperationException
{
    public MotionInterlockException(string message)
        : base(message)
    {
    }
}

public sealed class MotionException : Exception
{
    public MotionException(string operation, Exception innerException)
        : base(
            $"{operation} failed.",
            innerException)
    {
    }
}
