using System;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.Device;

[JsonConverter(typeof(JsonStringEnumConverter<BoltDriver>))]
public enum BoltDriver
{
    [Description("Virtual")]
    Virtual,

    [Description("I/O control + ADC results")]
    HantasAdc,

    // Accepted only when loading older driver settings.
    [Description("I/O control + ADC results")]
    Io,
}

public interface IBoltHead
{
    AdcStatusMonitor? Monitor { get; }

    Task CheckReadyAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
    Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default);
    // Feed starts only after START succeeds, inside the same STOP cleanup.
    // A positive dry-run duration replaces result waiting with timed motor operation.
    Task<BoltResult> TightenAsync(
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? feedAsync = null,
        int dryRunMilliseconds = 0);
}
