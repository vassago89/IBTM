using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class NgCarrierTransferSettings : Setting
{
    public NgCarrierTransferSettings()
    {
        CarrierPickupPosition = new();
        ShuttlePlacePosition = new();
    }

    public AxisPosition? WaitingPosition { get; set; }
    public double? PickupSafeX { get; set; }
    // Keep the stored fields unchanged: pickup X is PickupSafeX, pickup Y is here.
    public AxisPosition CarrierPickupPosition { get; set; }
    public AxisPosition ShuttlePlacePosition { get; set; }

    public AxisPosition? GetCarrierPickupPosition()
    {
        if (PickupSafeX is not { } x)
            return null;
        return new() { X = x, Y = CarrierPickupPosition.Y };
    }

}
