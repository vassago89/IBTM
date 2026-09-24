using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public sealed partial class ManualConveyorRow : ObservableObject
{
    private readonly ILogger<ManualConveyorRow>? _log;
    private readonly MachineController _machine;
    [ObservableProperty]
    public partial string? ActionMessage { get; set; }

    public ManualConveyorRow(
        IoOutputStatus io, MachineController machine, ILogger<ManualConveyorRow>? log = null)
    {
        RunCommand = new AsyncRelayCommand(RunAsync);
        StopCommand = new AsyncRelayCommand(StopAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);

        _log = log;
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
            _log?.LogError(exception, "Manual conveyor STOP failed.");
        }
    }
}
