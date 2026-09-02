using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.UI;

public partial class InputWindow : Window, INotifyPropertyChanged
{
    private readonly IIoService _io;
    private readonly VirtualIoService? _virtualIo;
    private readonly MachineState _state;

    public InputWindow(IIoService io, MachineState state)
    {
        _io = io;
        _state = state;
        _virtualIo = io as VirtualIoService;
        Rows = Enum.GetValues<InputIo>()
            .Select(input => new InputControlRow(io, input))
            .ToArray();

        InitializeComponent();
        DataContext = this;
        _io.InputChanged += OnInputChanged;
        _state.Changed += OnMachineStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public InputControlRow[] Rows { get; }
    public bool IsVirtual => _virtualIo is not null;
    public bool CanToggle => IsVirtual && !_state.IsRunning;

    protected override void OnClosed(EventArgs e)
    {
        _io.InputChanged -= OnInputChanged;
        _state.Changed -= OnMachineStateChanged;
        base.OnClosed(e);
    }

    private void OnToggleInput(object sender, RoutedEventArgs e)
    {
        var row = (InputControlRow)((Button)sender).DataContext;
        _virtualIo!.SetInput(row.Input, !row.IsOn);
    }

    private void OnInputChanged(InputIo input, bool value) =>
        Dispatcher.BeginInvoke((Action)(() =>
            Rows[(int)input].Set(value)));

    private void OnMachineStateChanged() =>
        Dispatcher.BeginInvoke((Action)(() =>
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(CanToggle)))));
}

public sealed class InputControlRow : ObservableObject
{
    private bool _isOn;

    public InputControlRow(IIoService io, InputIo input)
    {
        Input = input;
        _isOn = io.GetInput(input);
    }

    public InputIo Input { get; }
    public bool IsOn => _isOn;

    public void Set(bool value)
    {
        _isOn = value;
        OnPropertyChanged(nameof(IsOn));
    }
}
