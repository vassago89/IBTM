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

    public TeachingPosition[] GetTeachingPositions()
    {
        return [
            new(
                TeachingTarget.InspectionWaiting,
                MotionGroup.InspectionGantry,
                TeachMode.XYOnly,
                () => WaitingPosition ?? new(),
                p => WaitingPosition = p,
                this,
                () => WaitingPosition is not null),
            new(
                TeachingTarget.NgCarrierPickup,
                MotionGroup.InspectionGantry,
                TeachMode.XYOnly,
                () => GetCarrierPickupPosition() ?? new() { Y = CarrierPickupPosition.Y },
                p =>
                {
                    PickupSafeX = p.X;
                    CarrierPickupPosition.Y = p.Y;
                },
                this,
                () => PickupSafeX is not null),
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
