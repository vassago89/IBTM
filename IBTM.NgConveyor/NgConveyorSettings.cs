using IBTM.Core;

namespace IBTM.NgConveyor;

public sealed class NgConveyorSettings : Setting
{
    public int AlarmCarrierCount { get; set; } = 3;
    public double TransferSpeed { get; set; } = 100.0;
    public AxisPos CarrierPickupPosition { get; set; } = new();
    public AxisPos ShuttlePlacePosition { get; set; } = new();
}
