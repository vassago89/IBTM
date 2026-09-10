using System.Linq;
using System.Windows;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.UI;

public partial class InputWindow : Window
{
    public InputWindow(IIoService io, IoSignals signals)
    {
        VirtualIo = io as VirtualIoService;
        Signals = signals;
        Filter = new(
            signals.Inputs.Values.Select(signal => new InputControlRow(signal, VirtualIo)).ToArray(),
            row => row.Io,
            nameof(InputControlRow.Io));
        InitializeComponent();
        DataContext = this;
    }

    public VirtualIoService? VirtualIo { get; }
    public IoSignals Signals { get; }
    public IoList<InputControlRow, InputIo> Filter { get; }

    public bool IsVirtual
    {
        get
        {
            return VirtualIo is not null;
        }
    }
}
