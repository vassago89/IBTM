using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

// CanSet(false) uses display feedback; execution rechecks with CanSet(true).
public sealed record TeachingOutput(
    OutputIo Signal,
    HardwareArea Owner,
    Func<bool, CancellationToken, Task> SetAsync,
    Func<bool, bool>? CanSet = null,
    bool RequiresHandler = true,
    bool HoldToRun = false);
