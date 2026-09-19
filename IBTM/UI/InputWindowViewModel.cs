using System.Linq;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.UI;

public sealed class InputWindowViewModel
{
    public InputWindowViewModel(IIoService io, IoSignals signals)
    {
        VirtualIo = io as VirtualIoService;
        Signals = signals;
        Filter = new(
            signals.Inputs.Values.Select(signal => new InputControlRow(signal, VirtualIo)).ToArray(),
            row => row.Io);
    }

    public VirtualIoService? VirtualIo { get; }
    public IoSignals Signals { get; }
    public IoList<InputControlRow, InputIo> Filter { get; }

    public bool IsVirtual => VirtualIo is not null;
}
