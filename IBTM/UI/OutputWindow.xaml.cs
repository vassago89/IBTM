using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class OutputWindow : Window
{
    private readonly MachineState _state;
    private readonly MachineController _machine;

    public OutputWindow(IoSignals signals, MachineController machine, MachineState state)
    {
        _state = state;
        _machine = machine;
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

    public void Shutdown()
    {
        IsEnabled = false;
        _machine.StopRunOutputs();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        try
        {
            Shutdown();
        }
        catch (Exception exception)
        {
            e.Cancel = true;
            IsEnabled = true;
            System.Diagnostics.Trace.TraceError("Output window shutdown failed. {0}", exception);
            MessageBox.Show(
                this,
                exception.Message,
                "Output Shutdown Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _state.RequestDisplayRefresh();
        foreach (var row in Rows)
            row.ActionMessage = null;
    }

}
