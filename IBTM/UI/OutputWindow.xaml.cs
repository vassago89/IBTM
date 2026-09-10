using System.Linq;
using System.Windows;
using IBTM.Device;

namespace IBTM.UI;

public partial class OutputWindow : Window
{
    private readonly MachineState _state;

    public OutputWindow(IoSignals signals, MachineController machine, MachineState state)
    {
        _state = state;
        Rows = signals.Outputs.Values.OrderBy(row => row.Signal)
            .Select(row => new OutputWindowRow(row, machine))
            .ToArray();
        Filter = new(Rows, row => row.Io, nameof(OutputWindowRow.Io));

        InitializeComponent();
        DataContext = this;
        state.RequestDisplayRefresh();
    }

    public OutputWindowRow[] Rows { get; }
    public IoList<OutputWindowRow, OutputIo> Filter { get; }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _state.RequestDisplayRefresh();
        foreach (var row in Rows)
            row.ActionMessage = null;
    }

}
