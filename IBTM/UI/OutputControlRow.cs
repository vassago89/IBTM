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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFeedbackError))]
    private string? _feedbackError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ToggleHint))]
    private string? _actionMessage;
    [ObservableProperty]
    private OutputBlockReason _blockReason;

    public OutputControlRow(IoOutputStatus status, MachineController machine)
    {
        _machine = machine;
        Io = status;
        ToggleCommand.PropertyChanged += OnToggleCommandChanged;
    }

    public IoOutputStatus Io { get; }

    public bool HasFeedbackError
    {
        get
        {
            return FeedbackError is not null;
        }
    }

    public bool IsConveyorRun
    {
        get
        {
            return MachineController.IsConveyorRunOutput(Io.Signal);
        }
    }

    public bool IsMaintainedOutput
    {
        get
        {
            return IsConveyorRun || MachineController.IsInterfaceOutput(Io.Signal);
        }
    }

    public string ToggleHint
    {
        get
        {
            return ActionMessage ?? (IsMaintainedOutput
                ? "ON starts this output. OFF or closing this window stops it."
                : "Toggle this output.");
        }
    }

    // The button always calls this method. XAML only displays the observed IO state.
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private void Switch()
    {
        if (IsMaintainedOutput && (ToggleCommand.IsRunning || Io.IsOn == true))
            StopOutputTest();
        else if (ToggleCommand.CanExecute(null))
            ToggleCommand.Execute(null);
    }

    // Only prevents overlapping feedback waits; safety is checked by the controller.
    private bool CanSwitch()
    {
        return IsMaintainedOutput || !ToggleCommand.IsRunning;
    }

    [RelayCommand]
    private void StopOutputTest()
    {
        ToggleCommand.Cancel();
        if (IsMaintainedOutput)
            _machine.StopManualOutput(Io.Signal);
    }

    private void OnToggleCommandChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(IAsyncRelayCommand.IsRunning))
            return;
        SwitchCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ToggleAsync(CancellationToken cancellationToken)
    {
        Refresh();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlockReason = await _machine.ToggleManualOutputAsync(Io.Signal, cancellationToken);
            if (BlockReason != OutputBlockReason.None)
                ActionMessage = $"[{BlockReason}] {BlockReason.GetDescription()}";
        }
        catch (IoTimeoutException exception)
        {
            FeedbackError = $"{Io.Signal} [{Io.Address}]: {exception.Message}";
            ActionMessage = FeedbackError;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // The controller logs the hardware failure; the view only presents it.
            ActionMessage = exception.Message;
        }
    }

    public void Refresh()
    {
        FeedbackError = null;
        ActionMessage = null;
        BlockReason = OutputBlockReason.None;
    }
}
