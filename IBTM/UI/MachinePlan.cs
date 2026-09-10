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

    public static Thickness CarrierBorderThickness
    {
        get
        {
            return new(CarrierBorder);
        }
    }

    public static Thickness CarrierContentMargin
    {
        get
        {
            return new(CarrierPadding);
        }
    }

    public static GridLength HeatSinkGapWidth
    {
        get
        {
            return new(HeatSinkGap);
        }
    }

    public static GridLength SupplySlotGapWidth
    {
        get
        {
            return new(SupplySlotGap);
        }
    }

    public static Thickness SupplyCarrierMargin
    {
        get
        {
            return new(SupplyCarrierPadding);
        }
    }

    public static double CarrierContentInset
    {
        get
        {
            return CarrierBorder + CarrierPadding;
        }
    }

    public static double PlacementCarrierLeft
    {
        get
        {
            return FirstPlateLeft + PlatePadding;
        }
    }

    public static double FasteningPlateLeft
    {
        get
        {
            return FirstPlateLeft + StationPitch;
        }
    }

    public static double FasteningCarrierLeft
    {
        get
        {
            return FasteningPlateLeft + PlatePadding;
        }
    }

    public static double FasteningLeft
    {
        get
        {
            return MainLeft + FasteningPlateLeft - FasteningHeaderOffset;
        }
    }

    public static double PlacementStopperLeft
    {
        get
        {
            return PlacementCarrierLeft + CarrierWidth - 7;
        }
    }

    public static double FasteningStopperLeft
    {
        get
        {
            return PlacementStopperLeft + StationPitch;
        }
    }

    public static double InspectionStopperLeft
    {
        get
        {
            return PlacementStopperLeft + StationPitch * 2;
        }
    }

    public static double StopperTop
    {
        get
        {
            return PlateTop + (PlateHeight - 28) / 2 - 1;
        }
    }

    public static double FirstTransferLeft
    {
        get
        {
            return PlacementCarrierLeft + StationPitch / 2;
        }
    }

    public static double SecondTransferLeft
    {
        get
        {
            return FirstTransferLeft + StationPitch;
        }
    }

    public static double ShootingHeadLeft
    {
        get
        {
            return PickupHeadLeft + HeadPitch;
        }
    }

    public static double HeadCenterY
    {
        get
        {
            return HeadTop + HeadSize / 2;
        }
    }

    public static double PickupHeadCenterX
    {
        get
        {
            return PickupHeadLeft + HeadSize / 2;
        }
    }

    public static double BoltTargetRadius
    {
        get
        {
            return BoltTargetSize / 2;
        }
    }

    public static double BoltTargetOffset
    {
        get
        {
            return -BoltTargetRadius;
        }
    }

    public static Thickness PlacementToolMargin
    {
        get
        {
            return new(0, 0, 0, PlacementBottomMargin);
        }
    }

    public static (double X, double Y) BufferCenter
    {
        get
        {
            return (BufferLeft + BufferWidth / 2, BufferTop + BufferHeight / 2);
        }
    }

    public static (double X, double Y) SupplyToolCenter
    {
        get
        {
            return (SupplyWidth / 2, SupplyHeight - 1 - PcbHeight / 2);
        }
    }

    public static (double X, double Y) SupplyPcb1Center
    {
        get
        {
            return (
                SupplyRailLeft
                    + (SupplyRailWidth - SupplyCarrierFrameWidth) / 2
                    + CarrierBorder
                    + SupplyCarrierPadding
                    + (SupplyCarrierWidth - (CarrierBorder + SupplyCarrierPadding) * 2 - SupplySlotGap) / 4,
                SupplyRailTop + SupplyRailHeight / 2);
        }
    }

    public static (double X, double Y) SupplyPcb2Center
    {
        get
        {
            return (
                SupplyPcb1Center.X
                    + (SupplyCarrierWidth - (CarrierBorder + SupplyCarrierPadding) * 2 + SupplySlotGap) / 2,
                SupplyPcb1Center.Y);
        }
    }

    public static (double X, double Y) PlacementToolCenter
    {
        get
        {
            return (PlacementWidth / 2, PlacementHeight - PlacementBottomMargin - PcbHeight / 2);
        }
    }

    public static (double X, double Y) PlacementHeatSink1
    {
        get
        {
            return (
                PlacementCarrierLeft + CarrierContentInset + (CarrierWidth - CarrierContentInset * 2 - HeatSinkGap) / 4,
                MainTop + CarrierTop + CarrierHeight / 2);
        }
    }

    public static (double X, double Y) PlacementHeatSink2
    {
        get
        {
            return (
                PlacementCarrierLeft + CarrierWidth - (PlacementHeatSink1.X - PlacementCarrierLeft),
                PlacementHeatSink1.Y);
        }
    }

    public static (double X, double Y) PickupToolCenter
    {
        get
        {
            return (PickupHeadCenterX, HeadCenterY);
        }
    }

    public static (double X, double Y) ShootingToolCenter
    {
        get
        {
            return (ShootingHeadLeft + HeadSize / 2, HeadCenterY);
        }
    }

    public static (double X, double Y) FasteningUpperLeft
    {
        get
        {
            return (
                MainLeft + FasteningCarrierLeft - FasteningLeft + CarrierWorkInset,
                MainTop + CarrierTop + CarrierWorkInset);
        }
    }

    public static (double X, double Y) FasteningLowerRight
    {
        get
        {
            return (
                FasteningUpperLeft.X + CarrierWidth - CarrierWorkInset * 2,
                FasteningUpperLeft.Y + CarrierHeight - CarrierWorkInset * 2);
        }
    }

    public static (double X, double Y) FasteningContentOrigin
    {
        get
        {
            return (
                MainLeft + FasteningCarrierLeft - FasteningLeft + CarrierContentInset,
                MainTop + CarrierTop + CarrierContentInset);
        }
    }

    public static (double X, double Y) InspectionContentOrigin
    {
        get
        {
            return (
                InspectionCarrierCenter.X - CarrierWidth / 2 + CarrierContentInset,
                MainTop + CarrierTop + CarrierContentInset);
        }
    }

    public static double PlateWidth
    {
        get
        {
            return CarrierWidth + PlatePadding * 2;
        }
    }

    public static double PlateHeight
    {
        get
        {
            return CarrierHeight + PlatePadding * 2;
        }
    }

    public static double PcbSlotWidth
    {
        get
        {
            return PcbWidth + PcbSlotPadding * 2;
        }
    }

    public static double PcbSlotHeight
    {
        get
        {
            return PcbHeight + PcbSlotPadding * 2;
        }
    }

    public static double InspectionPlateLeft
    {
        get
        {
            return FirstPlateLeft + StationPitch * 2;
        }
    }

    public static double InspectionCarrierLeft
    {
        get
        {
            return InspectionPlateLeft + PlatePadding;
        }
    }

    public static double CarrierTop
    {
        get
        {
            return PlateTop + PlatePadding;
        }
    }

    public static double InspectionLeft
    {
        get
        {
            return MainLeft + InspectionPlateLeft - HeaderInset;
        }
    }

    public static double NgConveyorWidth
    {
        get
        {
            return PlateWidth;
        }
    }

    public static double NgPositionHeight
    {
        get
        {
            return CarrierHeight + NgPositionGap;
        }
    }

    public static double NgConveyorLeft
    {
        get
        {
            return InspectionCarrierCenter.X - NgConveyorWidth / 2;
        }
    }

    public static double NgStatusLeft
    {
        get
        {
            return NgConveyorLeft + NgConveyorWidth + PositionLabelGap;
        }
    }

    public static double StationLabelTop
    {
        get
        {
            return PlateTop + PlateHeight + 12;
        }
    }

    public static double InspectionResultsTop
    {
        get
        {
            return MainTop + StationLabelTop + 40;
        }
    }

    public static (double X, double Y) InspectionCarrierCenter
    {
        get
        {
            return (HeaderInset + PlateWidth / 2, MainTop + PlateTop + PlateHeight / 2);
        }
    }

    public static (double X, double Y) NgPickerCenter
    {
        get
        {
            return (CarrierWidth / 2, CarrierHeight / 2);
        }
    }

    public static (double X, double Y) CameraCenter
    {
        get
        {
            return (CarrierWidth / 2, CarrierHeight + CameraGap + CameraSize / 2);
        }
    }

    public static (double X, double Y) NgShuttleCenter
    {
        get
        {
            return (InspectionCarrierCenter.X, NgConveyorTop + PlatePadding + NgPositionHeight / 2);
        }
    }

    public static (double X, double Y) InspectionUpperLeft
    {
        get
        {
            return (
                InspectionCarrierCenter.X - CarrierWidth / 2 + CarrierWorkInset,
                InspectionCarrierCenter.Y - CarrierHeight / 2 + CarrierWorkInset);
        }
    }

    public static (double X, double Y) InspectionLowerRight
    {
        get
        {
            return (
                InspectionCarrierCenter.X + CarrierWidth / 2 - CarrierWorkInset,
                InspectionCarrierCenter.Y + CarrierHeight / 2 - CarrierWorkInset);
        }
    }

    public static (double X, double Y) Offset((double X, double Y) point, (double X, double Y) origin)
    {
        return (point.X - origin.X, point.Y - origin.Y);
    }

    public static double Side(
        (double X, double Y) point,
        (double X, double Y) first,
        (double X, double Y) second)
    {
        return (second.X - first.X) * (point.Y - first.Y) - (second.Y - first.Y) * (point.X - first.X);
    }
}
