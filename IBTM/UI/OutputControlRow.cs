using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
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

public sealed partial class OutputControlRow : ObservableObject
{
    private readonly MachineController _machine;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FeedbackState))] private string? _feedbackError;

    public OutputControlRow(IoOutputStatus status, MachineController machine)
    {
        _machine = machine;
        Io = status;
        PropertyChangedEventManager.AddHandler(status, OnIoChanged, string.Empty);
        ToggleCommand.PropertyChanged += OnToggleCommandChanged;
    }

    public IoOutputStatus Io { get; }
    public OutputFeedbackState FeedbackState =>
        FeedbackError is not null ? OutputFeedbackState.Timeout
        : Io.HasConflict ? OutputFeedbackState.Conflict
        : Io.HasFeedback && ToggleCommand.IsRunning ? OutputFeedbackState.Waiting
        : Io.IsOn is null ? OutputFeedbackState.Unknown
        : Io.IsMatched ? OutputFeedbackState.Matched
        : OutputFeedbackState.NotMatched;

    private bool IsConveyorRun => MachineController.IsConveyorRunOutput(Io.Signal);
    private bool IsOwnedOutputTest => IsConveyorRun
        || MachineController.IsInterfaceOutput(Io.Signal);
    private bool IsOwnedOutputTestRunning => ToggleCommand.IsRunning && IsOwnedOutputTest;
    private bool CanStopOutputTest => IsOwnedOutputTestRunning || IsConveyorRun && Io.IsOn == true;
    public IRelayCommand ActionCommand => CanStopOutputTest ? StopOutputTestCommand : ToggleCommand;
    public OutputBlockReason BlockReason => _machine.GetManualOutputBlock(Io.Signal);
    public string ToggleHint
    {
        get
        {
            if (CanStopOutputTest)
                return IsConveyorRun ? "Stop this conveyor motor." : "Send OFF to this interface output.";
            var reason = BlockReason;
            if (reason != OutputBlockReason.None) return $"[{reason}] {reason.GetDescription()}";
            return IsConveyorRun
                ? "Turn ON to run the empty conveyor forward at normal speed. OFF or closing this window stops the test."
                : MachineController.IsInterfaceOutput(Io.Signal)
                    ? "Confirm connected equipment is stopped. Keep ON until OFF, STOP, window close or an interlock change."
                    : "Toggle this output after rechecking live safety conditions.";
        }
    }
    // Show the action for every row consistently; an owned test stays cancellable
    // even before ON feedback arrives or while the machine is otherwise busy.
    public string ToggleLabel => (IsOwnedOutputTest ? CanStopOutputTest : Io.IsOn == true) ? "OFF" : "ON";
    private bool CanToggle() => BlockReason == OutputBlockReason.None;

    [RelayCommand(CanExecute = nameof(CanStopOutputTest))]
    private void StopOutputTest()
    {
        ToggleCommand.Cancel();
        if (IsConveyorRun) _machine.StopManualConveyor(Io.Signal);
    }

    private void OnToggleCommandChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(IAsyncRelayCommand.IsRunning)) return;
        RefreshAction();
    }

    private void RefreshAction()
    {
        StopOutputTestCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FeedbackState));
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
            await _machine.ToggleManualOutputAsync(Io.Signal, cancellationToken);
        }
        catch (IoTimeoutException exception)
        {
            FeedbackError = $"{Io.Signal} [{Io.Address}]: {exception.Message}";
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Refresh()
    {
        FeedbackError = null;
        RefreshAccess();
    }

    public void RefreshAccess()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(BlockReason));
        OnPropertyChanged(nameof(ToggleHint));
        OnPropertyChanged(nameof(ToggleLabel));
    }

    private void OnIoChanged(object? sender, PropertyChangedEventArgs args)
    {
        RefreshAction();
    }
}
