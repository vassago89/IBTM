using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public partial class OutputWindowViewModel : ObservableObject
{
    private readonly MachineState _state;

    public OutputWindowViewModel(IoSignals signals, MachineController machine, MachineState state)
    {
        _state = state;
        Rows = signals.Outputs.Values.OrderBy(row => row.Signal)
            .Select(row => new OutputWindowRow(row, machine))
            .ToArray();
        Filter = new(Rows, row => row.Io);

        state.RequestDisplayRefresh();
    }

    public OutputWindowRow[] Rows { get; }
    public IoList<OutputWindowRow, OutputIo> Filter { get; }

    [RelayCommand]
    private void Refresh()
    {
        _state.RequestDisplayRefresh();
        foreach (var row in Rows)
            row.ActionMessage = null;
    }
}
