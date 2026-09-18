using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class MotionMonitorAxis(
    MotionGroup group,
    MotionAxis axis,
    int number,
    MachineController machine,
    MachineState state,
    UnitSettings units) : ObservableObject
{
    private bool _lastEnabled = units.IsMotionEnabled(group);

    public MotionGroup Group { get; } = group;
    public MotionAxis Axis { get; } = axis;
    public int Number { get; } = number;
    public MotionDiagnostics Diagnostics { get; } = state.GetMotionStatus(group).MonitorAxes[axis];

    public string Address
    {
        get
        {
            return Number.ToString("D3", CultureInfo.InvariantCulture);
        }
    }

    public bool Enabled
    {
        get
        {
            return units.IsMotionEnabled(Group);
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleServo))]
    private void ToggleServo()
    {
        machine.ToggleServo(Group, Axis);
    }

    private bool CanToggleServo()
    {
        return Diagnostics.Snapshot.State is not null
            && machine.CanSetServo(Group, live: false);
    }

    [RelayCommand(CanExecute = nameof(CanHome), IncludeCancelCommand = true)]
    private Task HomeAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => machine.HomeAxisAsync(Group, Axis, cancellationToken));
    }

    private bool CanHome()
    {
        return Enabled
            && state.Display.Available
            && state.Display.HomeableAxes.Contains((Group, Axis));
    }

    internal bool Refresh()
    {
        var changed = SetProperty(ref _lastEnabled, Enabled, nameof(Enabled));
        ToggleServoCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        return changed;
    }
}
