using IBTM.Core;
using System.Text.Json.Serialization;

namespace IBTM.Inspection;

public sealed class NgCarrierTransferSettings : Setting, IJsonOnDeserialized
{
    private bool _hasLegacyPickupX;

    public NgCarrierTransferSettings()
    {
        ShuttlePlacePosition = new();
    }

    public AxisPosition? WaitingPosition { get; set; }
    public AxisPosition? CarrierPickupPosition { get; set; }
    public AxisPosition ShuttlePlacePosition { get; set; }

    // Read the old split coordinate once; new saves contain only CarrierPickupPosition.
    [JsonInclude, JsonPropertyName("PickupSafeX"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private double? LegacyPickupX
    {
        get;
        set
        {
            field = value;
            _hasLegacyPickupX = true;
        }
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (!_hasLegacyPickupX)
            return;
        CarrierPickupPosition = LegacyPickupX is { } x && CarrierPickupPosition is { } position
            ? new() { X = x, Y = position.Y } : null;
        LegacyPickupX = null;
        _hasLegacyPickupX = false;
    }
}
