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

    public Task ShutdownAsync()
    {
        // Application shutdown only. Leaving the page does not operate equipment.
        return CommandShutdown.StopAsync(
            null,
            Conveyors.SelectMany(row => new[] { row.StopCommand, row.RunCommand }).ToArray());
    }
}
