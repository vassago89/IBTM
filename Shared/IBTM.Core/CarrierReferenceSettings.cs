using System.Text.Json.Serialization;

namespace IBTM.Core;

public sealed class CarrierReferenceSettings : Setting
{
    public AxisPosition? UpperLeftLocatingPin { get; set; }
    public AxisPosition? LowerRightLocatingPin { get; set; }

    [JsonIgnore]
    public bool IsDefined
    {
        get
        {
            return CarrierCoordinates.IsDefined(UpperLeftLocatingPin, LowerRightLocatingPin);
        }
    }
}
