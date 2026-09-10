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

    [Description("Hantas ADC")]
    HantasAdc,
}

public interface IBoltHead
{
    // Result ownership only, not the physical head's ready/running state.
    bool HasPendingResult { get; }

    Task CheckReadyAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
    Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default);
    Task<BoltResult> TightenAsync(CancellationToken cancellationToken = default);
    void DiscardPendingResult();
}
