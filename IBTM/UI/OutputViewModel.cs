using System.Linq;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public class OutputViewModel
{
    public OutputViewModel(IoSignals signals, MachineController machine)
    {
        ClearMessagesCommand = new RelayCommand(ClearMessages);

        Rows = signals.Outputs.Values
            .Select(row => new OutputSignalRow(row, machine))
            .ToArray();
        Filter = new(Rows, row => row.Io);
    }

    public OutputSignalRow[] Rows { get; }
    public IoListViewModel<OutputSignalRow, OutputIo> Filter { get; }

    public IRelayCommand ClearMessagesCommand { get; }

    private void ClearMessages()
    {
        foreach (var row in Rows)
            row.ActionMessage = null;
    }
}
