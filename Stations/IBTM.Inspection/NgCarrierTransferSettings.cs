using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class NgCarrierTransferSettings : Setting
{
    public double Speed { get; set; } = 100.0;
    public double? PickupSafeX { get; set; }
    public AxisPosition CarrierPickupPosition { get; set; } = new();
    public AxisPosition ShuttlePlacePosition { get; set; } = new();

    public TeachingPosition[] GetTeachingPositions()
    {
        return [
            new(
                TeachingTarget.NgPickupSafeX,
                MotionGroup.InspectionGantry,
                TeachMode.XOnly,
                () => new() { X = PickupSafeX ?? 0 },
                p => PickupSafeX = p.X,
                this,
                () => PickupSafeX is not null),
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
