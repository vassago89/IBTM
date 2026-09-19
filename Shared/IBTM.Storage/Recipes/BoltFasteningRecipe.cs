using System.Text.Json.Serialization;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningRecipe
{
    public ushort PcbPreset { get; set; } = 1;

    // Preserve the configured fastening preset in existing recipe files.
    [JsonPropertyName("IpmFinalPreset")]
    public ushort PickupPreset { get; set; } = 2;
}
