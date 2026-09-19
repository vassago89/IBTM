using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.PcbSupply;

namespace IBTM.UI;

public sealed class ManualHardwareViewModel : ObservableObject
{
    public ManualHardwareViewModel(
        IoSignals signals,
        MachineController machine,
        PcbSupplyHandler supply,
        MainConveyor conveyor)
    {
        Signals = signals;
        Supply = supply;
        Conveyor = conveyor;
        supply.Changed += OnSupplyChanged;
        conveyor.Changed += OnConveyorChanged;
        Conveyors = [
            new(signals.Outputs[OutputIo.MainConveyorRun], machine),
            new(signals.Outputs[OutputIo.NgConveyorRun], machine),
        ];
    }

    public IoSignals Signals { get; }
    public PcbSupplyHandler Supply { get; }
    public MainConveyor Conveyor { get; }
    public ManualConveyorRow[] Conveyors { get; }

    private void OnSupplyChanged()
    {
        OnPropertyChanged(nameof(Supply));
    }

    private void OnConveyorChanged()
    {
        OnPropertyChanged(nameof(Conveyor));
    }

    public Task ShutdownAsync()
    {
        // Application shutdown only. Leaving the page does not operate equipment.
        return CommandShutdown.CancelAndWaitAsync(
            Conveyors.SelectMany(row => new[] { row.StopCommand, row.RunCommand }).ToArray());
    }
}
