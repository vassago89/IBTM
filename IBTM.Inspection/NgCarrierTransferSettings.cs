using IBTM.Core;

namespace IBTM.Inspection;

public sealed class NgCarrierTransferSettings : Setting
{
    public double Speed { get; set; } = 100.0;
    public AxisPosition CarrierPickupPosition { get; set; } = new();
    public AxisPosition ShuttlePlacePosition { get; set; } = new();
}
