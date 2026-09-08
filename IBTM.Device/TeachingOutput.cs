using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public sealed record TeachingOutput(
    OutputIo Signal,
    Func<bool, CancellationToken, Task> SetAsync,
    Func<bool>? CanSet = null,
    bool RequiresHandler = true,
    bool HoldToRun = false);
