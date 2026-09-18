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
        RunCommand = new AsyncRelayCommand(RunAsync);
        StopCommand = new AsyncRelayCommand(StopAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);

        _machine = machine;
        Io = io;
    }

    public IoOutputStatus Io { get; }

    public IAsyncRelayCommand RunCommand { get; }

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

    public IAsyncRelayCommand StopCommand { get; }

    private async Task StopAsync()
    {
        try
        {
            var pending = CommandShutdown.Capture(RunCommand);
            await CommandShutdown.CancelAndWaitAsync(
                [RunCommand],
                _machine.StopManualConveyorAsync(Io.Signal),
                pending);
        }
        catch (Exception exception)
        {
            ActionMessage = exception.Message;
            System.Diagnostics.Trace.TraceError("Manual conveyor STOP failed. {0}", exception);
        }
    }
}
