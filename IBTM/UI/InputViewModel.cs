using System.Linq;
using IBTM.Device;

namespace IBTM.UI;

public sealed class InputViewModel
{
    public InputViewModel(IoSignals signals)
    {
        Signals = signals;
        Filter = new(signals.Inputs.Values.ToArray(), signal => signal);
    }

    public IoSignals Signals { get; }
    public IoListViewModel<IoInputStatus, InputIo> Filter { get; }
}
