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

    public OutputWindow(IIoService io, IoSignals signals)
    {
        Rows = signals.Outputs.Values.OrderBy(row => row.Signal)
            .Select(row => new OutputControlRow(io, row)).ToArray();
        Filter = new(Rows, row => row.Io, nameof(OutputControlRow.Io));

        InitializeComponent();
        DataContext = this;
    }

    public OutputControlRow[] Rows { get; }
    public IoList<OutputControlRow, OutputIo> Filter { get; }

    public async Task ShutdownAsync()
    {
        var pending = CommandShutdown.Capture(Rows.Select(row => row.ToggleCommand).ToArray());
        IsEnabled = false;
        try
        {
            foreach (var row in Rows) row.ToggleCommand.Cancel();
        }
        finally
        {
            await CommandShutdown.WaitAsync(pending);
        }
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
            MessageBox.Show(this, exception.Message, "Output Shutdown Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows) row.Refresh();
    }

}

public sealed partial class OutputControlRow : ObservableObject
{
    private readonly IIoService _io;
    private bool _timedOut;
    private bool _waitingForFeedback;

    public OutputControlRow(IIoService io, IoOutputStatus status)
    {
        _io = io;
        Io = status;
        PropertyChangedEventManager.AddHandler(
            status, OnFeedbackChanged, nameof(IoOutputStatus.IsMatched));
    }

    public IoOutputStatus Io { get; }
    public OutputFeedbackState FeedbackState =>
        _timedOut ? OutputFeedbackState.Timeout
        : Io.HasConflict ? OutputFeedbackState.Conflict
        : Io.IsMatched ? OutputFeedbackState.Matched
        : _waitingForFeedback ? OutputFeedbackState.Waiting
        : OutputFeedbackState.NotMatched;

    [RelayCommand]
    private async Task ToggleAsync(CancellationToken cancellationToken)
    {
        var value = !Io.IsOn;
        Refresh();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _io.SetOutput(Io.Signal, value);
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
        NotifyFeedbackChanged();
    }

    public void Refresh()
    {
        _timedOut = false;
        NotifyFeedbackChanged();
    }

    private void OnFeedbackChanged(object? sender, PropertyChangedEventArgs args) => NotifyFeedbackChanged();

    private void NotifyFeedbackChanged() => OnPropertyChanged(nameof(FeedbackState));
}
