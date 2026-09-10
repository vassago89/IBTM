using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class NgCarrierTransferSettings : Setting
{
    public double Speed { get; set; } = 100.0;
    public double? PickupSafeX { get; set; }
    // Only Y is taught here. The pickup X is always PickupSafeX.
    public AxisPosition CarrierPickupPosition { get; set; } = new();
    public AxisPosition ShuttlePlacePosition { get; set; } = new();

    public AxisPosition? GetCarrierPickupPosition()
    {
        if (PickupSafeX is not { } x)
            return null;
        return new() { X = x, Y = CarrierPickupPosition.Y };
    }

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
                TeachMode.YOnly,
                () => CarrierPickupPosition,
                p => CarrierPickupPosition.Y = p.Y,
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
