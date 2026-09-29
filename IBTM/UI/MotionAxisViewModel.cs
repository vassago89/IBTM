using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed class MotionAxisViewModel : ObservableObject
{
    private readonly MachineController _machine;
    private readonly UnitSettings _units;
    private bool _lastEnabled;

    public MotionAxisViewModel(
        MotionGroup group,
        MotionAxis axis,
        int number,
        MachineController machine,
        MachineState state,
        UnitSettings units)
    {
        _machine = machine;

        ToggleServoCommand = new RelayCommand(ToggleServo, () => IsToggleServoAllowed);
        HomeCommand = new AsyncRelayCommand(HomeAsync, () => _machine.IsManualHomeAllowed(Group, Axis));

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
    public AxisDiagnostics Diagnostics { get; }

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
            return Diagnostics.Sample.State is not null
                && _machine.IsSetServoAllowed(Group);
        }
    }

    public IAsyncRelayCommand HomeCommand { get; }

    private Task HomeAsync(CancellationToken cancellationToken)
    {
        return _machine.HomeAsync(Group, cancellationToken, Axis);
    }

    internal bool Refresh()
    {
        var changed = SetProperty(ref _lastEnabled, Enabled, nameof(Enabled));
        ToggleServoCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        return changed;
    }
}
