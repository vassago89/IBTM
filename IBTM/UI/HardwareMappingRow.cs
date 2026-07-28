using System;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class HardwareMappingRow(
    Enum signal,
    int number,
    AxisDirection direction = AxisDirection.Positive) : ObservableObject
{
    public Enum Signal { get; } = signal;
    public string Name => Signal.ToString();
    public bool CanToggle => Signal is OutputIo;
    public bool CanHome => Signal is MachineAxis axis && axis != MachineAxis.Conveyor;
    public bool CanServo => Signal is MachineAxis;

    [ObservableProperty] private int _number = number;
    [ObservableProperty] private AxisDirection _direction = direction;
    [ObservableProperty] private string _state = "—";
}
