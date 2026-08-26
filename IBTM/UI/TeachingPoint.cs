using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public enum TeachMode
{
    [Description("Image")]
    Image,

    [Description("XYZ")]
    Full,

    [Description("XY")]
    XYOnly,

    [Description("XZ")]
    XZOnly,

    [Description("X")]
    XOnly,

    [Description("Y")]
    YOnly,

    [Description("Z")]
    ZOnly,
}

public enum TeachingStorage
{
    [Description("Recipe")]
    Recipe,

    [Description("Machine")]
    Machine,
}

public enum TeachingTarget
{
    [Description("Supply Rotation Z")]
    SupplyRotationZ,

    [Description("Supply Carrier Y")]
    SupplyCarrierY,

    [Description("PCB 1 Pick")]
    SupplyPcb1Pick,

    [Description("PCB 2 Pick")]
    SupplyPcb2Pick,

    [Description("Supply Buffer Handoff")]
    SupplyBufferHandoff,

    [Description("Supply Buffer Clear Z")]
    SupplyBufferClearZ,

    [Description("Placement Buffer Handoff")]
    PlacementBufferHandoff,

    [Description("Supply Buffer Boundary 1")]
    SupplyBufferBoundary1,

    [Description("Supply Buffer Boundary 2")]
    SupplyBufferBoundary2,

    [Description("Placement Buffer Boundary 1")]
    PlacementBufferBoundary1,

    [Description("Placement Buffer Boundary 2")]
    PlacementBufferBoundary2,

    [Description("Placement Buffer Entry Z")]
    PlacementBufferEntryZ,

    [Description("Housing 1 PCB Placement")]
    Housing1PcbPlacement,

    [Description("Housing 2 PCB Placement")]
    Housing2PcbPlacement,

    [Description("Bolt Work Z")]
    BoltWorkZ,

    [Description("Carrier Jig Scan Upper Left")]
    CarrierScanUpperLeft,

    [Description("Carrier Jig Scan Lower Right")]
    CarrierScanLowerRight,

    [Description("NG Carrier Jig Pickup")]
    NgCarrierJigPickup,

    [Description("NG Shuttle Place")]
    NgShuttlePlace,

    [Description("Carrier Jig Upper Left Locating Pin")]
    InspectionUpperLeftLocatingPin,

    [Description("Carrier Jig Lower Right Locating Pin")]
    InspectionLowerRightLocatingPin,

    [Description("Head 2 Shooting Upper Left Locating Pin")]
    ShootingHeadUpperLeftLocatingPin,

    [Description("Head 2 Shooting Lower Right Locating Pin")]
    ShootingHeadLowerRightLocatingPin,

    [Description("Head 1 Pickup Upper Left Locating Pin")]
    PickupHeadUpperLeftLocatingPin,

    [Description("Head 1 Pickup Lower Right Locating Pin")]
    PickupHeadLowerRightLocatingPin,

    [Description("Bolt Pickup")]
    BoltPickup,

    [Description("Bolt Fastening Safe Z")]
    BoltFasteningSafeZ,

    [Description("Bolt Reference")]
    BoltReference,
}

public partial class TeachingPoint : ObservableObject
{
    public TeachingTarget Target { get; init; }
    public MotionGroup MotionGroup { get; init; }
    public TeachMode TeachMode { get; init; }
    public TeachingStorage Storage { get; init; }
    public int BoltNumber { get; init; }
    public HousingSlot? Housing { get; init; }
    public FasteningHead? Head { get; init; }
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _z;

    public string Name => Target switch
    {
        TeachingTarget.BoltWorkZ or TeachingTarget.BoltReference => $"B{BoltNumber}",
        _ => Target.GetDescription(),
    };

    public string PositionLabel => TeachMode switch
    {
        TeachMode.Image => $"{X:F3}, {Y:F3}",
        TeachMode.XYOnly => $"{X:F1}, {Y:F1}",
        TeachMode.XZOnly => $"{X:F1}, {Z:F1}",
        TeachMode.XOnly => $"{X:F1}",
        TeachMode.YOnly => $"{Y:F1}",
        TeachMode.ZOnly => $"{Z:F1}",
        _ => $"{X:F1}, {Y:F1}, {Z:F1}",
    };

    public void Teach(double x, double y, double z)
    {
        switch (TeachMode)
        {
            case TeachMode.Image:
                X = x;
                Y = y;
                break;
            case TeachMode.Full:
                X = x;
                Y = y;
                Z = z;
                break;
            case TeachMode.XYOnly:
                X = x;
                Y = y;
                break;
            case TeachMode.XZOnly:
                X = x;
                Z = z;
                break;
            case TeachMode.XOnly:
                X = x;
                break;
            case TeachMode.YOnly:
                Y = y;
                break;
            case TeachMode.ZOnly:
                Z = z;
                break;
        }

    }

    partial void OnXChanged(double value) =>
        OnPropertyChanged(nameof(PositionLabel));

    partial void OnYChanged(double value) =>
        OnPropertyChanged(nameof(PositionLabel));

    partial void OnZChanged(double value) =>
        OnPropertyChanged(nameof(PositionLabel));
}
