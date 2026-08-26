using System;

namespace IBTM.Device;

public sealed class MotionException(
    string operation,
    Exception innerException) : Exception(
        $"{operation} failed.",
        innerException)
{ }
