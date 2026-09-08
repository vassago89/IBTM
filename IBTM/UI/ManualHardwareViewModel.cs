using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public enum ManualConveyorStatus
{
    [Description("Stopped")]
    Stopped,

    [Description("Running")]
    Running,
}

public sealed class ManualAxisRow(
    MotionGroup group,
    MotionAxis axis,
    MotionStatus motion)
{
    public MotionGroup Group { get; } = group;
    public MotionAxis Axis { get; } = axis;
    public MotionStatus Motion { get; } = motion;
    public AxisStatus Feedback { get; } = motion.Axes[axis];
    public bool IndividualHomeAvailable =>
        Group != MotionGroup.PcbSupply;
}

public partial class ManualHardwareViewModel : ObservableObject
{
    private readonly MainConveyor _conveyor;
    private readonly MachineState _state;
    private readonly MachineController _machine;
    private volatile bool _active;
    private int _stateRefreshQueued;

    public bool IsHoming => _state.Display.IsHoming;

    public ManualHardwareViewModel(
        MainConveyor conveyor,
        MachineState state,
        MachineController machine,
        IReadOnlyList<MotionHardwareSettings> hardware)
    {
        _conveyor = conveyor;
        _state = state;
        _machine = machine;
        Axes = hardware.SelectMany(section => section.AxisSignals.Keys.Select(axis =>
            new ManualAxisRow(section.Group, axis, machine.GetMotionStatus(section.Group)))).ToArray();

        state.DisplayChanged += OnMachineStateChanged;
    }

    public ManualAxisRow[] Axes { get; }
    public HomeBlockReason HomeBlock => _state.Display.HomeBlock;
    public ManualConveyorStatus ConveyorStatus =>
        _state.Display.ConveyorRunning
            ? ManualConveyorStatus.Running
            : ManualConveyorStatus.Stopped;

    [RelayCommand(CanExecute = nameof(CanRunConveyor))]
    private void RunConveyor() => _machine.RunManualConveyor();

    private bool CanRunConveyor() => _state.Display.ManualControlsEnabled;

    [RelayCommand]
    private void StopConveyor() => _conveyor.Stop();

    [RelayCommand(CanExecute = nameof(CanToggleServo))]
    private void ToggleServo(ManualAxisRow row) =>
        _machine.ToggleServo(row.Group, row.Axis);

    private bool CanToggleServo(ManualAxisRow? row) =>
        row is not null
        && _machine.CanSetServo(row.Group);

    [RelayCommand(CanExecute = nameof(CanHomeAxis))]
    private Task HomeAxisAsync(
        ManualAxisRow row,
        CancellationToken cancellationToken) =>
        _machine.HomeAxisAsync(row.Group, row.Axis, cancellationToken);

    private bool CanHomeAxis(ManualAxisRow? row) =>
        row is not null && _state.Display.HomeableAxes.Contains((row.Group, row.Axis));

    [RelayCommand(CanExecute = nameof(CanStopHome))]
    private void StopHome() => HomeAxisCommand.Cancel();

    private bool CanStopHome() => IsHoming;

    public void Activate()
    {
        _active = true;
        _state.RequestDisplayRefresh();
        OnMachineStateChanged();
    }

    public void Deactivate()
    {
        _active = false;
        StopHome();
        _conveyor.Stop();
    }

    public Task ShutdownAsync() =>
        CommandShutdown.StopAsync(Deactivate, HomeAxisCommand);

    private void OnMachineStateChanged()
    {
        if (!_active)
        {
            return;
        }

        if (Interlocked.Exchange(ref _stateRefreshQueued, 1) != 0)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _stateRefreshQueued, 0);
            if (!_active)
            {
                return;
            }

            OnPropertyChanged(nameof(IsHoming));
            OnPropertyChanged(nameof(ConveyorStatus));
            OnPropertyChanged(nameof(HomeBlock));
            RunConveyorCommand.NotifyCanExecuteChanged();
            ToggleServoCommand.NotifyCanExecuteChanged();
            HomeAxisCommand.NotifyCanExecuteChanged();
            StopHomeCommand.NotifyCanExecuteChanged();
        });
    }

}
