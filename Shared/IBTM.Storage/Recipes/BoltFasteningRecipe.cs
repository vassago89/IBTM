using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IBTM;

public sealed class BoltFasteningRecipe
{
    public BoltFasteningRecipe()
    {
        ShootingIpmFeed = new();
        PickupIpmFeed = new();
        ShootingPcbFeed = new();
    }

    public FasteningFeed ShootingIpmFeed { get; set; }
    public FasteningFeed PickupIpmFeed { get; set; }
    public FasteningFeed ShootingPcbFeed { get; set; }
}

public sealed partial class FasteningFeed : ObservableObject
{
    public FasteningFeed()
    {
        Seconds = 1;
    }

    [ObservableProperty]
    public partial double Distance { get; set; }

    [ObservableProperty]
    public partial double Seconds { get; set; }

    partial void OnDistanceChanging(double value)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Use a finite distance of zero or greater.");
    }

    partial void OnSecondsChanging(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Use a positive finite time in seconds.");
    }
}
