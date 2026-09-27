using System.Linq;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public class OutputWindowViewModel
{
    public OutputWindowViewModel(IoSignals signals, MachineController machine)
    {
        ClearMessagesCommand = new RelayCommand(ClearMessages);

        Rows = signals.Outputs.Values
            .Select(row => new OutputWindowRow(row, machine))
            .ToArray();
        Filter = new(Rows, row => row.Io);
    }

    public OutputWindowRow[] Rows { get; }
    public IoList<OutputWindowRow, OutputIo> Filter { get; }

    public IRelayCommand ClearMessagesCommand { get; }

    private void ClearMessages()
    {
        foreach (var row in Rows)
            row.ActionMessage = null;
    }
}
