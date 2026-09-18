using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class ManualConveyorRow : ObservableObject
{
    private readonly MachineController _machine;
    [ObservableProperty]
    private string? _actionMessage;

    public ManualConveyorRow(IoOutputStatus io, MachineController machine)
    {
        _machine = machine;
        Io = io;
    }

    public IoOutputStatus Io { get; }

    [RelayCommand]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        ActionMessage = null;
        try
        {
            var reason = await _machine.RunManualConveyorAsync(Io.Signal, cancellationToken);
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

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StopAsync()
    {
        try
        {
            await CommandShutdown.StopAsync(
                () => _machine.StopManualConveyor(Io.Signal),
                RunCommand);
        }
        catch (Exception exception)
        {
            ActionMessage = exception.Message;
            System.Diagnostics.Trace.TraceError("Manual conveyor STOP failed. {0}", exception);
        }
    }
}
