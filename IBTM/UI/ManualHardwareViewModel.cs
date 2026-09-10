using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Device;

namespace IBTM.UI;

public sealed class ManualHardwareViewModel : ObservableObject
{
    public ManualHardwareViewModel(IoSignals signals, MachineController machine)
    {
        Conveyors = [
            new(signals.Outputs[OutputIo.MainConveyorRun], machine),
            new(signals.Outputs[OutputIo.NgConveyorRun], machine),
        ];
    }

    public ManualConveyorRow[] Conveyors { get; }

    public void Deactivate()
    {
        foreach (var row in Conveyors)
            row.RunCommand.Cancel();
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.StopAsync(
            Deactivate,
            [.. Conveyors.Select(row => row.RunCommand)]);
    }
}
