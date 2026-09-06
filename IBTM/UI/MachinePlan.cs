namespace IBTM.UI;

// Design dimensions live here; tool centres and station anchors are derived from them.
public static class MachinePlan
{
    public const double PcbWidth = 60;
    public const double PcbHeight = 48;
    public const double PcbSlotPadding = 4;
    public const double CarrierWidth = 268;
    public const double CarrierHeight = 84;
    public const double PlatePadding = 8;
    public const double CameraSize = 48;
    public const double CameraGap = 20;
    public const double NgPositionGap = 6;
    public const double PositionLabelGap = 12;
    public const double CarrierWorkInset = 18;
    public const double StationPitch = 410;
    public const double MainLeft = 24;
    public const double MainTop = 360;
    public const double FirstPlateLeft = 194;
    public const double PlateTop = 70;
    public const double HeaderInset = 20;
    public const double HeaderTop = 14;
    public const double StatusTop = 52;
    public const double DetailTop = 108;
    public const double InspectionWidth = 498;
    public const double PlanHeight = 820;
    public const double NgConveyorTop = 132;

    public static double PlateWidth => CarrierWidth + PlatePadding * 2;
    public static double PlateHeight => CarrierHeight + PlatePadding * 2;
    public static double PcbSlotWidth => PcbWidth + PcbSlotPadding * 2;
    public static double PcbSlotHeight => PcbHeight + PcbSlotPadding * 2;
    public static double InspectionPlateLeft => FirstPlateLeft + StationPitch * 2;
    public static double InspectionCarrierLeft => InspectionPlateLeft + PlatePadding;
    public static double CarrierTop => PlateTop + PlatePadding;
    public static double InspectionLeft => MainLeft + InspectionPlateLeft - HeaderInset;
    public static double NgConveyorWidth => PlateWidth;
    public static double NgPositionHeight => CarrierHeight + NgPositionGap;
    public static double NgConveyorLeft => InspectionCarrierCenter.X - NgConveyorWidth / 2;
    public static double NgStatusLeft => NgConveyorLeft + NgConveyorWidth + PositionLabelGap;
    public static double StationLabelTop => PlateTop + PlateHeight + 12;

    public static (double X, double Y) InspectionCarrierCenter =>
        (HeaderInset + PlateWidth / 2, MainTop + PlateTop + PlateHeight / 2);
    public static (double X, double Y) NgPickerCenter => (CarrierWidth / 2, CarrierHeight / 2);
    public static (double X, double Y) CameraCenter =>
        (CarrierWidth / 2, CarrierHeight + CameraGap + CameraSize / 2);
    public static (double X, double Y) NgShuttleCenter =>
        (InspectionCarrierCenter.X, NgConveyorTop + PlatePadding + NgPositionHeight / 2);
    public static (double X, double Y) InspectionUpperLeft =>
        (InspectionCarrierCenter.X - CarrierWidth / 2 + CarrierWorkInset,
         InspectionCarrierCenter.Y - CarrierHeight / 2 + CarrierWorkInset);
    public static (double X, double Y) InspectionLowerRight =>
        (InspectionCarrierCenter.X + CarrierWidth / 2 - CarrierWorkInset,
         InspectionCarrierCenter.Y + CarrierHeight / 2 - CarrierWorkInset);

    public static (double X, double Y) Offset(
        (double X, double Y) point, (double X, double Y) origin) =>
        (point.X - origin.X, point.Y - origin.Y);

    public static double Side(
        (double X, double Y) point,
        (double X, double Y) first,
        (double X, double Y) second) =>
        (second.X - first.X) * (point.Y - first.Y)
        - (second.Y - first.Y) * (point.X - first.X);
}
