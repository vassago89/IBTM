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

    [Description("ADC communication")]
    HantasAdc,

    [Description("IO only")]
    Io,
}

public interface IBoltHead
{
    // Result ownership only, not the physical head's ready/running state.
    bool HasPendingResult { get; }
    Task CheckReadyAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
    Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default);
    // Feed starts only after START succeeds, inside the same timeout and STOP cleanup.
    // Collecting an existing result does not feed again.
    Task<BoltResult> TightenAsync(
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? feedAsync = null);
    // Read an outstanding result without starting or stopping the motor.
    Task<BoltResult?> ReadPendingResultAsync(CancellationToken cancellationToken = default);
    void DiscardPendingResult();
}
