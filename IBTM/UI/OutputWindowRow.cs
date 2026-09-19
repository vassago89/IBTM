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
    public partial string? ActionMessage { get; set; }

    public OutputWindowRow(IoOutputStatus io, MachineController machine)
    {
        ToggleCommand = new RelayCommand(Toggle, () => IsToggleAllowed);

        _machine = machine;
        Io = io;
    }

    public IoOutputStatus Io { get; }

    private bool IsToggleAllowed => Io.Signal != OutputIo.PcbPlacementHandlerRotate;

    public IRelayCommand ToggleCommand { get; }

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
