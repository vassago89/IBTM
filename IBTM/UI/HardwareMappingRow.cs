using System;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class HardwareMappingRow(
    Enum signal,
    int number,
    AxisDirection direction = AxisDirection.Positive,
    double minimum = 0,
    double maximum = 0) : ObservableObject
{
    public Enum Signal { get; } = signal;
    public string Name => Signal.GetDescription();
    public bool CanHome => Signal is MachineAxis axis && axis != MachineAxis.Conveyor;
    public bool CanServo => Signal is MachineAxis;
    public bool CanSetRange => CanHome;

    [ObservableProperty] private int _number = number;
    [ObservableProperty] private AxisDirection _direction = direction;
    [ObservableProperty] private double _minimum = minimum;
    [ObservableProperty] private double _maximum = maximum;
    [ObservableProperty] private string _state = "—";
    [ObservableProperty] private AxisDisplayState _axisDisplayState;
}
