using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Device;

namespace IBTM.UI;

public enum OutputFeedbackState
{
    [Description("Unknown")] Unknown,
    [Description("Not matched")] NotMatched,
    [Description("Matched")] Matched,
    [Description("Waiting")] Waiting,
    [Description("Input conflict")] Conflict,
    [Description("Timeout")] Timeout,
}

public partial class OutputWindow : Window
{
    private bool _closing;
    private bool _shutdownCompleted;
    private readonly MachineState _state;

    public OutputWindow(IIoService io, IoSignals signals, MachineController machine, MachineState state)
    {
        _state = state;
        Rows = signals.Outputs.Values.OrderBy(row => row.Signal)
            .Select(row => new OutputControlRow(io, row, machine)).ToArray();
        Filter = new(Rows, row => row.Io, nameof(OutputControlRow.Io));

        InitializeComponent();
        DataContext = this;
    }

    public OutputControlRow[] Rows { get; }
    public IoList<OutputControlRow, OutputIo> Filter { get; }

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

public sealed partial class OutputControlRow : ObservableObject
{
    private readonly IIoService _io;
    private readonly MachineController _machine;
    private bool _timedOut;
    private bool _waitingForFeedback;

    public OutputControlRow(IIoService io, IoOutputStatus status, MachineController machine)
    {
        _io = io;
        _machine = machine;
        Io = status;
        PropertyChangedEventManager.AddHandler(
            status, OnFeedbackChanged, nameof(IoOutputStatus.IsMatched));
    }

    public IoOutputStatus Io { get; }
    public OutputFeedbackState FeedbackState =>
        _timedOut ? OutputFeedbackState.Timeout
        : Io.HasConflict ? OutputFeedbackState.Conflict
        : _waitingForFeedback ? OutputFeedbackState.Waiting
        : Io.IsOn is null ? OutputFeedbackState.Unknown
        : Io.IsMatched ? OutputFeedbackState.Matched
        : OutputFeedbackState.NotMatched;

    [RelayCommand]
    private async Task ToggleAsync(CancellationToken cancellationToken)
    {
        Refresh();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_machine.ToggleManualOutput(Io.Signal) is not { } value) return;
            if (Io.HasFeedback)
            {
                SetWaiting(true);
                await _io.WaitForOutputFeedbackAsync(Io.Signal, value, cancellationToken);
            }
        }
        catch (IoTimeoutException)
        {
            _timedOut = true;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetWaiting(false);
        }
    }

    private void SetWaiting(bool value)
    {
        _waitingForFeedback = value;
        OnPropertyChanged(nameof(FeedbackState));
    }

    public void Refresh()
    {
        _timedOut = false;
        OnPropertyChanged(nameof(FeedbackState));
    }

    private void OnFeedbackChanged(object? sender, PropertyChangedEventArgs args) =>
        OnPropertyChanged(nameof(FeedbackState));
}
