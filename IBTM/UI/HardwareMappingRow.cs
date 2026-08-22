using System;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class HardwareMappingRow(
    HardwareArea area,
    Enum signal,
    int number,
    Action<HardwareMappingRow> apply,
    AxisDirection direction = AxisDirection.Positive,
    double minimum = 0,
    double maximum = 0,
    Func<bool>? readInput = null,
    Func<AxisState>? readAxisState = null) : ObservableObject
{
    public HardwareArea Area { get; } = area;
    public Enum Signal { get; } = signal;
    [ObservableProperty] private int _number = number;
    [ObservableProperty] private int? _offNumber;
    [ObservableProperty] private AxisDirection _direction = direction;
    [ObservableProperty] private double _minimum = minimum;
    [ObservableProperty] private double _maximum = maximum;
    public bool IsOn => readInput?.Invoke() ?? false;
    public AxisState AxisState => readAxisState?.Invoke() ?? default;

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(AxisState));
    }

    public void Apply() => apply(this);
}
