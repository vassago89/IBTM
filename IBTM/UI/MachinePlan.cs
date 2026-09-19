using System.Windows;

namespace IBTM.UI;
// Design dimensions live here; tool centres and station anchors are derived from them.
public static class MachinePlan
{
    public const double PcbWidth = 60;
    public const double PcbHeight = 48;
    public const double PcbSlotPadding = 4;
    public const double CarrierWidth = 268;
    public const double CarrierHeight = 84;
    public const double NgPickerWidth = 100;
    public const double PlatePadding = 8;
    public const double CameraSize = 48;
    public const double CameraGap = 20;
    public const double NgPositionGap = 6;
    public const double PositionLabelGap = 12;
    public const double CarrierWorkInset = 18;
    public const double StationPitch = 410;
    public const double MainLeft = 24;
    public const double MainTop = 360;
    public const double RearInterfaceLeft = 1320;
    public const double FirstPlateLeft = 194;
    public const double PlateTop = 70;
    public const double HeaderInset = 20;
    public const double HeaderTop = 14;
    public const double StatusTop = 52;
    public const double DetailTop = 108;
    public const double InspectionWidth = 498;
    public const double PlanHeight = 820;
    public const double NgConveyorTop = 132;
    public const double CarrierBorder = 2;
    public const double CarrierPadding = 9;
    public const double HeatSinkGap = 9;
    public const double BoltTargetSize = 20;
    public const double FasteningHeaderOffset = 18;
    public const double HeadSize = 34;
    public const double HeadTop = 84;
    public const double PickupHeadLeft = 20.5;
    public const double HeadPitch = 51;
    public const double BufferLeft = 276;
    public const double BufferTop = 236;
    public const double BufferWidth = 116;
    public const double BufferHeight = 84;
    public const double SupplyWidth = 100;
    public const double SupplyHeight = 112;
    public const double PlacementWidth = 104;
    public const double PlacementHeight = 106;
    public const double PlacementBottomMargin = 13;
    public const double SupplyRailLeft = 54;
    public const double SupplyRailTop = 164;
    public const double SupplyRailWidth = 220;
    public const double SupplyRailHeight = 108;
    public const double SupplyCarrierFrameWidth = 208;
    public const double SupplyCarrierWidth = 192;
    public const double SupplyCarrierHeight = 66;
    public const double SupplyCarrierPadding = 5;
    public const double SupplySlotGap = 6;
    public const double PickupFeederWidth = 100;
    public const double PickupFeederHeight = 68;

    public const double CarrierContentInset = CarrierBorder + CarrierPadding;
    public const double PlacementCarrierLeft = FirstPlateLeft + PlatePadding;
    public const double FasteningPlateLeft = FirstPlateLeft + StationPitch;
    public const double FasteningCarrierLeft = FasteningPlateLeft + PlatePadding;
    public const double FasteningLeft = MainLeft + FasteningPlateLeft - FasteningHeaderOffset;
    public const double PlacementStopperLeft = PlacementCarrierLeft + CarrierWidth - 7;
    public const double FasteningStopperLeft = PlacementStopperLeft + StationPitch;
    public const double InspectionStopperLeft = PlacementStopperLeft + StationPitch * 2;
    public const double StopperTop = PlateTop + (PlateHeight - 28) / 2 - 1;
    public const double ShootingHeadLeft = PickupHeadLeft + HeadPitch;
    public const double HeadCenterY = HeadTop + HeadSize / 2;
    public const double PickupHeadCenterX = PickupHeadLeft + HeadSize / 2;
    public const double BoltTargetRadius = BoltTargetSize / 2;
    public const double BoltTargetOffset = -BoltTargetRadius;
    public const double PlateWidth = CarrierWidth + PlatePadding * 2;
    public const double PlateHeight = CarrierHeight + PlatePadding * 2;
    public const double PcbSlotWidth = PcbWidth + PcbSlotPadding * 2;
    public const double PcbSlotHeight = PcbHeight + PcbSlotPadding * 2;
    public const double InspectionPlateLeft = FirstPlateLeft + StationPitch * 2;
    public const double InspectionCarrierLeft = InspectionPlateLeft + PlatePadding;
    public const double CarrierTop = PlateTop + PlatePadding;
    public const double InspectionLeft = MainLeft + InspectionPlateLeft - HeaderInset;
    public const double NgConveyorWidth = PlateWidth;
    public const double NgPositionHeight = CarrierHeight + NgPositionGap;
    public const double StationLabelTop = PlateTop + PlateHeight + 12;
    public const double InspectionResultsTop = MainTop + 340;
    public const double InspectionResultsLeft = MainLeft + RearInterfaceLeft - InspectionLeft;

    static MachinePlan()
    {
        CarrierBorderThickness = new(CarrierBorder);
        CarrierContentMargin = new(CarrierPadding);
        HeatSinkGapWidth = new(HeatSinkGap);
        SupplySlotGapWidth = new(SupplySlotGap);
        SupplyCarrierMargin = new(SupplyCarrierPadding);
        PlacementToolMargin = new(0, 0, 0, PlacementBottomMargin);
        BufferCenter = (BufferLeft + BufferWidth / 2, BufferTop + BufferHeight / 2);
        SupplyToolCenter = (SupplyWidth / 2, SupplyHeight - 1 - PcbHeight / 2);
        SupplyPcb1Center = (
            SupplyRailLeft
                + (SupplyRailWidth - SupplyCarrierFrameWidth) / 2
                + CarrierBorder
                + SupplyCarrierPadding
                + (SupplyCarrierWidth - (CarrierBorder + SupplyCarrierPadding) * 2 - SupplySlotGap) / 4,
            SupplyRailTop + SupplyRailHeight / 2);
        PlacementToolCenter = (
            PlacementWidth / 2,
            PlacementHeight - PlacementBottomMargin - PcbHeight / 2);
        PlacementHeatSink1 = (
            PlacementCarrierLeft + CarrierContentInset + (CarrierWidth - CarrierContentInset * 2 - HeatSinkGap) / 4,
            MainTop + CarrierTop + CarrierHeight / 2);
        PickupToolCenter = (PickupHeadCenterX, HeadCenterY);
        ShootingToolCenter = (ShootingHeadLeft + HeadSize / 2, HeadCenterY);
        FasteningUpperLeft = (
            MainLeft + FasteningCarrierLeft - FasteningLeft + CarrierWorkInset,
            MainTop + CarrierTop + CarrierWorkInset);
        FasteningContentOrigin = (
            MainLeft + FasteningCarrierLeft - FasteningLeft + CarrierContentInset,
            MainTop + CarrierTop + CarrierContentInset);
        InspectionCarrierCenter = (
            HeaderInset + PlateWidth / 2,
            MainTop + PlateTop + PlateHeight / 2);
        NgPickerCenter = (CarrierWidth / 2, CarrierHeight / 2);
        CameraCenter = (
            CarrierWidth / 2,
            CarrierHeight + CameraGap + CameraSize / 2);
        SupplyPcb2Center = (
            SupplyPcb1Center.X
                + (SupplyCarrierWidth - (CarrierBorder + SupplyCarrierPadding) * 2 + SupplySlotGap) / 2,
            SupplyPcb1Center.Y);
        PlacementHeatSink2 = (
            PlacementCarrierLeft + CarrierWidth - (PlacementHeatSink1.X - PlacementCarrierLeft),
            PlacementHeatSink1.Y);
        FasteningLowerRight = (
            FasteningUpperLeft.X + CarrierWidth - CarrierWorkInset * 2,
            FasteningUpperLeft.Y + CarrierHeight - CarrierWorkInset * 2);
        InspectionContentOrigin = (
            InspectionCarrierCenter.X - CarrierWidth / 2 + CarrierContentInset,
            MainTop + CarrierTop + CarrierContentInset);
        NgConveyorLeft = InspectionCarrierCenter.X - NgConveyorWidth / 2;
        NgShuttleCenter = (
            InspectionCarrierCenter.X,
            NgConveyorTop + PlatePadding + NgPositionHeight / 2);
        InspectionUpperLeft = (
            InspectionCarrierCenter.X - CarrierWidth / 2 + CarrierWorkInset,
            InspectionCarrierCenter.Y - CarrierHeight / 2 + CarrierWorkInset);
        InspectionLowerRight = (
            InspectionCarrierCenter.X + CarrierWidth / 2 - CarrierWorkInset,
            InspectionCarrierCenter.Y + CarrierHeight / 2 - CarrierWorkInset);
        NgStatusLeft = NgConveyorLeft + NgConveyorWidth + PositionLabelGap;
    }

    public static Thickness CarrierBorderThickness { get; }

    public static Thickness CarrierContentMargin { get; }

    public static GridLength HeatSinkGapWidth { get; }

    public static GridLength SupplySlotGapWidth { get; }

    public static Thickness SupplyCarrierMargin { get; }

    public static Thickness PlacementToolMargin { get; }

    public static (double X, double Y) BufferCenter { get; }

    public static (double X, double Y) SupplyToolCenter { get; }

    public static (double X, double Y) SupplyPcb1Center { get; }

    public static (double X, double Y) PlacementToolCenter { get; }

    public static (double X, double Y) PlacementHeatSink1 { get; }

    public static (double X, double Y) PickupToolCenter { get; }

    public static (double X, double Y) ShootingToolCenter { get; }

    public static (double X, double Y) FasteningUpperLeft { get; }

    public static (double X, double Y) FasteningContentOrigin { get; }

    public static (double X, double Y) InspectionCarrierCenter { get; }

    public static (double X, double Y) NgPickerCenter { get; }

    public static (double X, double Y) CameraCenter { get; }

    public static (double X, double Y) SupplyPcb2Center { get; }

    public static (double X, double Y) PlacementHeatSink2 { get; }

    public static (double X, double Y) FasteningLowerRight { get; }

    public static (double X, double Y) InspectionContentOrigin { get; }

    public static double NgConveyorLeft { get; }

    public static (double X, double Y) NgShuttleCenter { get; }

    public static (double X, double Y) InspectionUpperLeft { get; }

    public static (double X, double Y) InspectionLowerRight { get; }

    public static double NgStatusLeft { get; }

    public static (double X, double Y) Offset((double X, double Y) point, (double X, double Y) origin)
    {
        return (point.X - origin.X, point.Y - origin.Y);
    }

    public static double GetSide(
        (double X, double Y) point,
        (double X, double Y) first,
        (double X, double Y) second)
    {
        return (second.X - first.X) * (point.Y - first.Y) - (second.Y - first.Y) * (point.X - first.X);
    }
}
