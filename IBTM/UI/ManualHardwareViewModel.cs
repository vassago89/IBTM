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

    public async Task ShutdownAsync()
    {
        // Application shutdown only. Leaving the page does not operate equipment.
        var commands = Conveyors.Select(row => row.RunCommand).ToArray();
        var pending = CommandShutdown.Capture(commands);
        foreach (var command in commands)
            command.Cancel();
        await CommandShutdown.WaitAsync(pending);
    }
}
