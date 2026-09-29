using System.Linq;
using IBTM.Device;
using IBTM.Virtual;

namespace IBTM.UI;

public sealed class InputViewModel
{
    public InputViewModel(IIoService io, IoSignals signals)
    {
        VirtualIo = io as VirtualIoService;
        Signals = signals;
        Filter = new(
            signals.Inputs.Values.Select(signal => new InputSignalRow(signal, VirtualIo)).ToArray(),
            row => row.Io);
    }

    public VirtualIoService? VirtualIo { get; }
    public IoSignals Signals { get; }
    public IoListViewModel<InputSignalRow, InputIo> Filter { get; }

    public bool IsVirtual => VirtualIo is not null;
}
