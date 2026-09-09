using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class OutputControlRow : ObservableObject
{
    private readonly MachineController _machine;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFeedbackError))] private string? _feedbackError;

    public OutputControlRow(IoOutputStatus status, MachineController machine)
    {
        _machine = machine;
        Io = status;
        ToggleCommand.PropertyChanged += OnToggleCommandChanged;
    }

    public IoOutputStatus Io { get; }
    public bool HasFeedbackError => FeedbackError is not null;

    public bool IsConveyorRun => MachineController.IsConveyorRunOutput(Io.Signal);
    public bool IsMaintainedOutput => IsConveyorRun
        || MachineController.IsInterfaceOutput(Io.Signal);
    private bool CanStopOutputTest => IsMaintainedOutput && ToggleCommand.IsRunning
        || IsConveyorRun && Io.IsOn == true;
    public OutputBlockReason BlockReason => _machine.GetManualOutputBlock(Io.Signal);
    public string ToggleHint
    {
        get
        {
            var reason = BlockReason;
            if (reason != OutputBlockReason.None) return $"[{reason}] {reason.GetDescription()}";
            return IsConveyorRun
                ? "Turn ON to run the conveyor motor forward at normal speed. OFF or closing this window stops the motor."
                : MachineController.IsInterfaceOutput(Io.Signal)
                    ? "Confirm connected equipment is stopped. Keep ON until OFF, STOP, window close or an interlock change."
                    : "Toggle this output after rechecking live safety conditions.";
        }
    }
    [RelayCommand(CanExecute = nameof(CanStopOutputTest))]
    private void StopOutputTest()
    {
        ToggleCommand.Cancel();
        if (IsConveyorRun) _machine.StopManualConveyor(Io.Signal);
    }

    private void OnToggleCommandChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(IAsyncRelayCommand.IsRunning)) return;
        StopOutputTestCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
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

    // Only command admission is refreshed by the owning view's UI callback.
    // Output and feedback presentation bind directly to Io and ToggleCommand.
    public void RefreshAccess()
    {
        ToggleCommand.NotifyCanExecuteChanged();
        StopOutputTestCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(BlockReason));
        OnPropertyChanged(nameof(ToggleHint));
    }
}
