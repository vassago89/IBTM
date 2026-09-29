using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.Device;

public interface IBoltHead
{
    AdcStatusMonitor? Monitor { get; }

    Task CheckReadyAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
    Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default);
    // Feed starts only after START succeeds, inside the same STOP cleanup.
    // A positive dry-run duration replaces result waiting with timed motor operation.
    // Deliver a measured result before STOP cleanup so its owner can retain it if cleanup fails.
    Task<BoltResult> TightenAsync(
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? feedAsync = null,
        int dryRunMilliseconds = 0,
        Action<BoltResult>? resultReceived = null);
}
