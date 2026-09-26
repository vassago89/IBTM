using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed class MotionMonitorAxis : ObservableObject
{
    private readonly MachineController _machine;
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
        ToggleServoCommand = new RelayCommand(ToggleServo, () => IsToggleServoAllowed);
        HomeCommand = new AsyncRelayCommand(HomeAsync, () => IsHomeAllowed);

        _machine = machine;
        _units = units;
        _lastEnabled = _units.IsMotionEnabled(group);
        Group = group;
        Axis = axis;
        Number = number;
        Diagnostics = state.GetMotionStatus(group).MonitorAxes[axis];
    }

    public MotionGroup Group { get; }
    public MotionAxis Axis { get; }
    public int Number { get; }
    public MotionDiagnostics Diagnostics { get; }

    public string Address => Number.ToString("D3", CultureInfo.InvariantCulture);

    public bool Enabled => _units.IsMotionEnabled(Group);

    public IRelayCommand ToggleServoCommand { get; }

    private void ToggleServo()
    {
        _machine.ToggleServo(Group, Axis);
    }

    private bool IsToggleServoAllowed
    {
        get
        {
            return Diagnostics.Snapshot.State is not null
                && _machine.IsSetServoAllowed(Group, live: false);
        }
    }

    public IAsyncRelayCommand HomeCommand { get; }

    private async Task HomeAsync(CancellationToken cancellationToken)
    {
        await _machine.HomeAsync(Group, cancellationToken, Axis);
    }

    private bool IsHomeAllowed => _machine.IsHomeAxisAllowed(Group, Axis);

    internal bool Refresh()
    {
        var changed = SetProperty(ref _lastEnabled, Enabled, nameof(Enabled));
        ToggleServoCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        return changed;
    }
}
