using System;

namespace IBTM.Device;

// Expected refusal from current equipment feedback; keep programming errors distinct.
public sealed class MotionInterlockException(string message) : InvalidOperationException(message)
{
}

public sealed class MotionException(string operation, Exception innerException) : Exception(
    $"{operation} failed.",
    innerException)
{
}
