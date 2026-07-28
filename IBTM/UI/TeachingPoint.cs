using System;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Stations.BoltFastening;

namespace IBTM.UI;

public enum TeachMode
{
    Full,
    XOnly,
    XZOnly,
    XYOnly,
    ZOnly,
}

public enum TeachingStorage
{
    Recipe,
    Machine,
}

public enum TeachingSection
{
    SupplyPositions,
    HandoffPair,
    StationPositions,
}

public enum TeachingTarget
{
    PcbSupplyCarrier1,
    PcbSupplyCarrier2,
    PcbSupplyRotation,
    PcbSupplyHandoff,
    PcbPlacementHandoff,
    Fiducial1,
    Fiducial2,
    Pcb1Place,
    Pcb2Place,
    BoltZ,
    Pcb1Inspection,
    Pcb2Inspection,
    NgCarrierPickup,
    NgStack,
    BoltPcb1Reference,
    BoltPcb2Reference,
    BoltReference,
}

public partial class TeachingPoint : ObservableObject
{
    public TeachingTarget Target { get; init; }
    public EquipmentUnit Unit { get; init; }
    public TeachMode TeachMode { get; init; }
    public TeachingStorage Storage { get; init; }
    public int BoltNumber { get; init; }
    public BoltType? BoltType { get; init; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _z;
    [ObservableProperty] private bool _isTaught;

    public TeachingSection Section => Target switch
    {
        TeachingTarget.PcbSupplyCarrier1
            or TeachingTarget.PcbSupplyCarrier2
            or TeachingTarget.PcbSupplyRotation =>
            TeachingSection.SupplyPositions,
        TeachingTarget.PcbSupplyHandoff
            or TeachingTarget.PcbPlacementHandoff =>
            TeachingSection.HandoffPair,
        _ => TeachingSection.StationPositions,
    };

    public string SectionLabel => Section switch
    {
        TeachingSection.SupplyPositions => "SUPPLY POSITIONS",
        TeachingSection.HandoffPair => "HANDOFF PAIR",
        TeachingSection.StationPositions => "STATION POSITIONS",
        _ => throw new ArgumentOutOfRangeException(nameof(Section)),
    };

    public string Name => Target switch
    {
        TeachingTarget.BoltZ or TeachingTarget.BoltReference => $"B{BoltNumber}",
        TeachingTarget.PcbSupplyCarrier1 => "Carrier Pick 1",
        TeachingTarget.PcbSupplyCarrier2 => "Carrier Pick 2",
        TeachingTarget.PcbSupplyRotation => "Rotation",
        TeachingTarget.PcbSupplyHandoff => "Supply Pose",
        TeachingTarget.PcbPlacementHandoff => "Placement Pick Pose",
        _ => Target.ToString(),
    };

    public string UnitLabel => Unit switch
    {
        EquipmentUnit.PcbSupply => "SUPPLY",
        EquipmentUnit.PcbPlacement => "PLACEMENT",
        _ => Unit.ToString().ToUpperInvariant(),
    };

    public string ModeLabel => TeachMode switch
    {
        TeachMode.Full => "XYZ",
        TeachMode.XOnly => "X",
        TeachMode.XZOnly => "XZ",
        TeachMode.XYOnly => "XY",
        TeachMode.ZOnly => "Z",
        _ => throw new ArgumentOutOfRangeException(nameof(TeachMode)),
    };

    public string PositionLabel => TeachMode switch
    {
        TeachMode.XOnly => $"{X:F1}",
        TeachMode.XZOnly => $"{X:F1}, {Z:F1}",
        TeachMode.XYOnly => $"{X:F1}, {Y:F1}",
        TeachMode.ZOnly => $"{Z:F1}",
        _ => $"{X:F1}, {Y:F1}, {Z:F1}",
    };

    public string BoltTypeLabel => BoltType switch
    {
        Stations.BoltFastening.BoltType.Standard => "STD",
        Stations.BoltFastening.BoltType.Loctite => "LOC",
        null => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(BoltType)),
    };

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
