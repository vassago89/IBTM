using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class ManualConveyorRow(IoOutputStatus io, MachineController machine) : ObservableObject
{
    [ObservableProperty]
    private string? _actionMessage;

    public IoOutputStatus Io { get; } = io;

    [RelayCommand]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        ActionMessage = null;
        try
        {
            var reason = await machine.RunManualConveyorAsync(Io.Signal, cancellationToken);
            if (reason != OutputBlockReason.None)
                ActionMessage = $"[{reason}] {reason.GetDescription()}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // The controller records the hardware error; this is the displayed result.
            ActionMessage = exception.Message;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        RunCommand.Cancel();
        machine.StopManualConveyor(Io.Signal);
    }
}
