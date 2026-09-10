using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class OutputWindowRow(IoOutputStatus io, MachineController machine) : ObservableObject
{
    [ObservableProperty]
    private string? _actionMessage;

    public IoOutputStatus Io { get; } = io;

    [RelayCommand]
    private void Toggle()
    {
        ActionMessage = null;
        try
        {
            var reason = machine.ToggleDiagnosticOutput(Io.Signal);
            if (reason != OutputBlockReason.None)
                ActionMessage = $"[{reason}] {reason.GetDescription()}";
        }
        catch (Exception exception)
        {
            // Hardware errors are logged by the controller, not hidden by the UI.
            ActionMessage = exception.Message;
        }
    }
}
