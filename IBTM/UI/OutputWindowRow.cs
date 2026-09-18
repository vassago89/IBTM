using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class OutputWindowRow : ObservableObject
{
    private readonly MachineController _machine;
    [ObservableProperty]
    private string? _actionMessage;

    public OutputWindowRow(IoOutputStatus io, MachineController machine)
    {
        _machine = machine;
        Io = io;
    }

    public IoOutputStatus Io { get; }

    [RelayCommand]
    private void Toggle()
    {
        ActionMessage = null;
        try
        {
            var reason = _machine.ToggleDiagnosticOutput(Io.Signal);
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
