using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class NgCarrierTransferSettings : Setting
{
    public double Speed { get; set; } = 100.0;
    public AxisPosition CarrierPickupPosition { get; set; } = new();
    public AxisPosition ShuttlePlacePosition { get; set; } = new();

    public TeachingPosition[] GetTeachingPositions()
    {
        return [
            new(
                TeachingTarget.NgCarrierPickup,
                MotionGroup.InspectionGantry,
                TeachMode.XYOnly,
                () => CarrierPickupPosition,
                p => CarrierPickupPosition = p,
                this),
            new(
                TeachingTarget.NgShuttlePlace,
                MotionGroup.InspectionGantry,
                TeachMode.XYOnly,
                () => ShuttlePlacePosition,
                p => ShuttlePlacePosition = p,
                this),
        ];
    }
}
