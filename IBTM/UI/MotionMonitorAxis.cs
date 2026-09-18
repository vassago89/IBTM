using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed partial class MotionMonitorAxis : ObservableObject
{
    private readonly MachineController _machine;
    private readonly MachineState _state;
    private readonly UnitSettings _units;
    private bool _lastEnabled;

    public MotionMonitorAxis(
        MotionGroup group,
        MotionAxis axis,
        int number,
        MachineController machine,
        MachineState state,
        UnitSettings units)
    {
        _machine = machine;
        _state = state;
        _units = units;
        _lastEnabled = _units.IsMotionEnabled(group);
        Group = group;
        Axis = axis;
        Number = number;
        Diagnostics = _state.GetMotionStatus(group).MonitorAxes[axis];
    }

    public MotionGroup Group { get; }
    public MotionAxis Axis { get; }
    public int Number { get; }
    public MotionDiagnostics Diagnostics { get; }

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
            return _units.IsMotionEnabled(Group);
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleServo))]
    private void ToggleServo()
    {
        _machine.ToggleServo(Group, Axis);
    }

    private bool CanToggleServo()
    {
        return Diagnostics.Snapshot.State is not null
            && _machine.CanSetServo(Group, live: false);
    }

    [RelayCommand(CanExecute = nameof(CanHome), IncludeCancelCommand = true)]
    private async Task HomeAsync(CancellationToken cancellationToken)
    {
        await _machine.HomeAxisAsync(Group, Axis, cancellationToken);
    }

    private bool CanHome()
    {
        return Enabled
            && _state.Display.Available
            && _state.Display.HomeableAxes.Contains((Group, Axis));
    }

    internal bool Refresh()
    {
        var changed = SetProperty(ref _lastEnabled, Enabled, nameof(Enabled));
        ToggleServoCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        return changed;
    }
}
