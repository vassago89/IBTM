using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IBTM.Presentation.Models;

public enum TeachMode
{
    Full,
    XYOnly,
    ZOnly,
}

public enum TeachingPointKind
{
    Zone1PcbPick,
    Zone1PcbPlaceZ,
    Zone2Fiducial,
    Zone2BoltZ,
    Zone3Inspection,
    Zone3NgPickup,
    Zone3NgPlace,
    Zone3PlaceReference,
    Zone3BoltReference,
}

public partial class TeachingPoint : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public TeachingPointKind Kind { get; init; }
    public int Zone { get; init; }
    public TeachMode TeachMode { get; init; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _z;
    [ObservableProperty] private bool _isTaught;

    public string ModeLabel => TeachMode switch
    {
        TeachMode.Full => "XYZ",
        TeachMode.XYOnly => "XY",
        TeachMode.ZOnly => "Z",
        _ => throw new ArgumentOutOfRangeException(nameof(TeachMode)),
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
}
