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
    Task<ushort> ReadTorqueCompensationAsync(ushort preset, CancellationToken cancellationToken = default);
    // Feed starts only after fresh RUN ON feedback following START, inside the same STOP cleanup.
    // The optional feed delay starts after RUN ON; completion/failure prevents subsequent feed.
    // A positive dry-run duration replaces result waiting with timed motor operation.
    // Deliver a measured result before STOP cleanup so its owner can retain it if cleanup fails.
    // Pass the preset setting captured at operation start; tightening does not query it.
    Task<BoltResult> TightenAsync(
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? feedAsync = null,
        int dryRunMilliseconds = 0,
        Action<BoltResult>? resultReceived = null,
        ushort? torqueCompensationPercent = null,
        int feedDelayMilliseconds = 0);
}
