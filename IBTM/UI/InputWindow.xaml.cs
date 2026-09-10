using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.UI;

public partial class InputWindow : Window, INotifyPropertyChanged
{
    private readonly VirtualIoService? _virtualIo;
    private readonly IoSignals _signals;

    public InputWindow(IIoService io, IoSignals signals)
    {
        _virtualIo = io as VirtualIoService;
        _signals = signals;
        signals.InputAvailabilityChanged += OnInputAvailabilityChanged;
        Filter = new(
            signals.Inputs.Values.Select(signal => new InputControlRow(signal, _virtualIo)).ToArray(),
            row => row.Io,
            nameof(InputControlRow.Io));
        InitializeComponent();
        if (_virtualIo is not null)
            _virtualIo.AutoResponseChanged += OnAutoResponseChanged;
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IoList<InputControlRow, InputIo> Filter { get; }

    public bool IsVirtual
    {
        get
        {
            return _virtualIo is not null;
        }
    }

    public bool InputsAvailable
    {
        get
        {
            return _signals.InputsAvailable;
        }
    }

    public bool AutoResponseEnabled
    {
        get
        {
            return _virtualIo?.AutoResponseEnabled ?? false;
        }

        set
        {
            if (_virtualIo is not null)
                _virtualIo.AutoResponseEnabled = value;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _signals.InputAvailabilityChanged -= OnInputAvailabilityChanged;
        if (_virtualIo is not null)
            _virtualIo.AutoResponseChanged -= OnAutoResponseChanged;
        base.OnClosed(e);
    }

    private void OnInputAvailabilityChanged()
    {
        PropertyChanged?.Invoke(this, new(nameof(InputsAvailable)));
    }

    private void OnAutoResponseChanged()
    {
        PropertyChanged?.Invoke(this, new(nameof(AutoResponseEnabled)));
    }
}
