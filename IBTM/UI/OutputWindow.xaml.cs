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

public partial class OutputWindow : Window, INotifyPropertyChanged
{
    private bool _closing;
    private bool _shutdownCompleted;
    private readonly MachineState _state;
    private int _refreshQueued;

    public OutputWindow(IIoService io, IoSignals signals, MachineController machine, MachineState state)
    {
        _state = state;
        Rows = signals.Outputs.Values.OrderBy(row => row.Signal)
            .Select(row => new OutputControlRow(io, row, machine)).ToArray();
        Filter = new(Rows, row => row.Io, nameof(OutputControlRow.Io));

        InitializeComponent();
        DataContext = this;
        state.DisplayChanged += OnDisplayChanged;
        state.RequestDisplayRefresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public OutputControlRow[] Rows { get; }
    public IoList<OutputControlRow, OutputIo> Filter { get; }
    public string ControlStatus => _state.Display.DiagnosticOutputBlock
        ?? "MANUAL output control · individual output interlocks apply · existing alarms remain latched.";

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

public sealed partial class OutputControlRow : ObservableObject
{
    private readonly MachineController _machine;
    private bool _timedOut;
    private bool _waitingForFeedback;
    [ObservableProperty] private string? _feedbackError;

    public OutputControlRow(IIoService io, IoOutputStatus status, MachineController machine)
    {
        _machine = machine;
        Io = status;
        PropertyChangedEventManager.AddHandler(
            status, OnFeedbackChanged, nameof(IoOutputStatus.IsMatched));
        ToggleCommand.PropertyChanged += OnToggleCommandChanged;
    }

    public IoOutputStatus Io { get; }
    public OutputFeedbackState FeedbackState =>
        _timedOut ? OutputFeedbackState.Timeout
        : Io.HasConflict ? OutputFeedbackState.Conflict
        : _waitingForFeedback ? OutputFeedbackState.Waiting
        : Io.IsOn is null ? OutputFeedbackState.Unknown
        : Io.IsMatched ? OutputFeedbackState.Matched
        : OutputFeedbackState.NotMatched;

    private bool IsOwnedOutputTestRunning => ToggleCommand.IsRunning
        && (Io.Signal == OutputIo.MainConveyorRun || MachineController.IsDiagnosticInterfaceOutput(Io.Signal));
    public IRelayCommand ActionCommand => IsOwnedOutputTestRunning ? StopOutputTestCommand : ToggleCommand;
    public string ToggleHint => IsOwnedOutputTestRunning
        ? Io.Signal == OutputIo.MainConveyorRun ? "Stop this main conveyor motor test." : "Send OFF to this interface output."
        : _machine.GetManualOutputBlock(Io.Signal)
        ?? (Io.Signal == OutputIo.MainConveyorRun
            ? "Run the empty conveyor forward at normal speed until STOP. Closing this window also stops the test."
            : MachineController.IsDiagnosticInterfaceOutput(Io.Signal)
                ? "Confirm connected equipment is stopped. Keep ON until OFF, STOP, window close or an interlock change."
                : "Toggle this output after rechecking live safety conditions.");
    public string ToggleLabel => Io.Signal == OutputIo.MainConveyorRun ? IsOwnedOutputTestRunning ? "STOP" : "RUN"
        : MachineController.IsDiagnosticInterfaceOutput(Io.Signal) ? IsOwnedOutputTestRunning ? "OFF" : "ON" : "Toggle";
    private bool CanToggle() => _machine.GetManualOutputBlock(Io.Signal) is null;

    [RelayCommand(CanExecute = nameof(IsOwnedOutputTestRunning))]
    private void StopOutputTest() => ToggleCommand.Cancel();

    private void OnToggleCommandChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(IAsyncRelayCommand.IsRunning)) return;
        StopOutputTestCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ActionCommand));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(ToggleHint));
    }

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleAsync(CancellationToken cancellationToken)
    {
        Refresh();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Io.HasFeedback) SetWaiting(true);
            await _machine.ToggleManualOutputAsync(Io.Signal, cancellationToken);
        }
        catch (IoTimeoutException exception)
        {
            _timedOut = true;
            FeedbackError = $"{Io.Signal} [{Io.Address}]: {exception.Message}";
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
        FeedbackError = null;
        OnPropertyChanged(nameof(FeedbackState));
        RefreshAccess();
    }

    public void RefreshAccess()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ToggleHint));
    }

    private void OnFeedbackChanged(object? sender, PropertyChangedEventArgs args) =>
        OnPropertyChanged(nameof(FeedbackState));
}
