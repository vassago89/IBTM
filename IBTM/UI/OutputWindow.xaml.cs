using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class OutputWindow : Window, INotifyPropertyChanged
{
    private bool _closing;
    private bool _shutdownCompleted;
    private readonly MachineState _state;
    private int _refreshQueued;

    public OutputWindow(IoSignals signals, MachineController machine, MachineState state)
    {
        _state = state;
        Rows = signals.Outputs.Values.OrderBy(row => row.Signal)
            .Select(row => new OutputControlRow(row, machine)).ToArray();
        Filter = new(Rows, row => row.Io, nameof(OutputControlRow.Io));

        InitializeComponent();
        DataContext = this;
        state.DisplayChanged += OnDisplayChanged;
        state.RequestDisplayRefresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public OutputControlRow[] Rows { get; }
    public IoList<OutputControlRow, OutputIo> Filter { get; }
    public string ControlStatus => _state.Display.ManualOutputBlock is var reason && reason != OutputBlockReason.None
        ? $"[{reason}] {reason.GetDescription()}"
        : "MANUAL output control · individual output interlocks apply · existing alarms remain latched.";

    private void OnDisplayChanged()
    {
        if (_closing || _shutdownCompleted || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_closing || _shutdownCompleted) return;
            PropertyChanged?.Invoke(this, new(nameof(ControlStatus)));
            foreach (var row in Rows) row.RefreshAccess();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _state.DisplayChanged -= OnDisplayChanged;
        base.OnClosed(e);
    }

    public Task ShutdownAsync()
    {
        IsEnabled = false;
        return CommandShutdown.StopAsync(
            () => { foreach (var row in Rows) row.ToggleCommand.Cancel(); },
            Rows.Select(row => row.ToggleCommand).ToArray());
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);
        if (_closing) return;
        _closing = true;
        try
        {
            await ShutdownAsync();
            _shutdownCompleted = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception exception)
        {
            _closing = false;
            IsEnabled = true;
            System.Diagnostics.Trace.TraceError("Output window shutdown failed. {0}", exception);
            MessageBox.Show(this, exception.Message, "Output Shutdown Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _state.RequestDisplayRefresh();
        foreach (var row in Rows) row.Refresh();
    }

}
