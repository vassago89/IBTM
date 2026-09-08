using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.UI;

public partial class InputWindow : Window, INotifyPropertyChanged
{
    private readonly VirtualIoService? _virtualIo;

    public InputWindow(IIoService io, IoSignals signals)
    {
        _virtualIo = io as VirtualIoService;
        Filter = new(signals.Inputs.Values.ToArray(), row => row);
        InitializeComponent();
        if (_virtualIo is not null)
            _virtualIo.AutoResponseChanged += OnAutoResponseChanged;
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IoList<IoSignal<InputIo>, InputIo> Filter { get; }
    public bool IsVirtual => _virtualIo is not null;
    public bool AutoResponseEnabled
    {
        get => _virtualIo?.AutoResponseEnabled ?? false;
        set
        {
            if (_virtualIo is not null) _virtualIo.AutoResponseEnabled = value;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_virtualIo is not null)
            _virtualIo.AutoResponseChanged -= OnAutoResponseChanged;
        base.OnClosed(e);
    }

    private void OnToggleInput(object sender, RoutedEventArgs e)
    {
        var row = (IoSignal<InputIo>)((Button)sender).DataContext;
        _virtualIo!.SetInput(row.Signal, row.IsOn != true);
    }

    private void OnAutoResponseChanged() =>
        Dispatcher.BeginInvoke((Action)(() =>
            PropertyChanged?.Invoke(this, new(nameof(AutoResponseEnabled)))));
}
