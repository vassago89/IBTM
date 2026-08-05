using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;
using IBTM.Stations.BoltFastening;

namespace IBTM.UI;

public enum TeachMode
{
    [Description("XYZ")]
    Full,

    [Description("X")]
    XOnly,

    [Description("XZ")]
    XZOnly,

    [Description("XY")]
    XYOnly,

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

public enum TeachingSection
{
    [Description("Supply Handler Positions")]
    SupplyPositions,

    [Description("Buffer Pair")]
    BufferPair,

    [Description("Station Positions")]
    StationPositions,
}

public enum TeachingTarget
{
    [Description("PCB 1 Pick")]
    SupplyPcb1Pick,

    [Description("PCB 2 Pick")]
    SupplyPcb2Pick,

    [Description("Supply Buffer")]
    SupplyBuffer,

    [Description("Placement Buffer")]
    PlacementBuffer,

    [Description("Fiducial 1 Capture")]
    Fiducial1Capture,

    [Description("Fiducial 2 Capture")]
    Fiducial2Capture,

    [Description("PCB 1 Placement")]
    Pcb1Placement,

    [Description("PCB 2 Placement")]
    Pcb2Placement,

    [Description("Bolt Work Z")]
    BoltWorkZ,

    [Description("PCB 1 Inspection")]
    Pcb1InspectionCapture,

    [Description("PCB 2 Inspection")]
    Pcb2InspectionCapture,

    [Description("NG Carrier Jig Pickup")]
    NgCarrierJigPickup,

    [Description("NG Shuttle")]
    NgShuttle,

    [Description("Bolt PCB 1 Reference")]
    BoltPcb1Reference,

    [Description("Bolt PCB 2 Reference")]
    BoltPcb2Reference,

    [Description("Loctite Bolt Pickup")]
    LoctiteBoltPickup,

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
    public FasteningHead? Head { get; init; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _z;
    [ObservableProperty] private bool _isTaught;

    public TeachingSection Section => Target switch
    {
        TeachingTarget.SupplyPcb1Pick
            or TeachingTarget.SupplyPcb2Pick =>
            TeachingSection.SupplyPositions,
        TeachingTarget.SupplyBuffer
            or TeachingTarget.PlacementBuffer =>
            TeachingSection.BufferPair,
        _ => TeachingSection.StationPositions,
    };

    public string SectionLabel => Section.GetDescription();

    public string Name => Target switch
    {
        TeachingTarget.BoltWorkZ or TeachingTarget.BoltReference => $"B{BoltNumber}",
        _ => Target.GetDescription(),
    };

    public string MotionGroupLabel => MotionGroup.GetDescription();

    public string ModeLabel => TeachMode.GetDescription();

    public string PositionLabel => TeachMode switch
    {
        TeachMode.XOnly => $"{X:F1}",
        TeachMode.XZOnly => $"{X:F1}, {Z:F1}",
        TeachMode.XYOnly => $"{X:F1}, {Y:F1}",
        TeachMode.ZOnly => $"{Z:F1}",
        _ => $"{X:F1}, {Y:F1}, {Z:F1}",
    };

    public string HeadLabel =>
        Head?.GetDescription() ?? string.Empty;

    public void Teach(double x, double y, double z)
    {
        switch (TeachMode)
        {
            case TeachMode.Full:
                X = x;
                Y = y;
                Z = z;
                break;
            case TeachMode.XOnly:
                X = x;
                break;
            case TeachMode.XZOnly:
                X = x;
                Z = z;
                break;
            case TeachMode.XYOnly:
                X = x;
                Y = y;
                break;
            case TeachMode.ZOnly:
                Z = z;
                break;
        }

        IsTaught = true;
    }

    partial void OnXChanged(double value) =>
        OnPropertyChanged(nameof(PositionLabel));

    partial void OnYChanged(double value) =>
        OnPropertyChanged(nameof(PositionLabel));

    partial void OnZChanged(double value) =>
        OnPropertyChanged(nameof(PositionLabel));
}
