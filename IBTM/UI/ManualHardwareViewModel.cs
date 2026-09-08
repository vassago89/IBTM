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

public enum ManualAxisStatus
{
    [Description("Ready")]
    Ready,

    [Description("Moving")]
    Moving,

    [Description("Servo Off")]
    ServoOff,

    [Description("Home Required")]
    HomeRequired,

    [Description("Negative Limit")]
    NegativeLimit,

    [Description("Positive Limit")]
    PositiveLimit,

    [Description("Alarm")]
    Alarm,

    [Description("Emergency")]
    Emergency,

    [Description("Unavailable")]
    Unavailable,
}

public enum ManualConveyorStatus
{
    [Description("Stopped")]
    Stopped,

    [Description("Running")]
    Running,
}

public sealed class ManualAxisRow(
    MotionGroup group,
    MachineAxis signal,
    MotionAxis axis,
    MotionStatus motion) : ObservableObject
{
    private AxisState _axisState = motion.Feedback.GetAxisState(axis);

    public MotionGroup Group { get; } = group;
    public MachineAxis Signal { get; } = signal;
    public MotionAxis Axis { get; } = axis;
    public MotionStatus Motion { get; } = motion;
    internal IMotionFeedback Feedback => Motion.Feedback;
    public bool ServoOn => _axisState.ServoOn;
    public bool IndividualHomeAvailable =>
        Group != MotionGroup.PcbSupply;
    public ManualAxisStatus Status => !Feedback.IsReady
        ? ManualAxisStatus.Unavailable
        : _axisState switch
        {
            { Emergency: true } => ManualAxisStatus.Emergency,
            { Alarm: true } => ManualAxisStatus.Alarm,
            { NegativeLimit: true } => ManualAxisStatus.NegativeLimit,
            { PositiveLimit: true } => ManualAxisStatus.PositiveLimit,
            { ServoOn: false } => ManualAxisStatus.ServoOff,
            { Homed: false } => ManualAxisStatus.HomeRequired,
            { InPosition: false } => ManualAxisStatus.Moving,
            _ => ManualAxisStatus.Ready,
        };

    internal void RefreshState()
    {
        _axisState = Feedback.GetAxisState(Axis);
        OnPropertyChanged(nameof(ServoOn));
        OnPropertyChanged(nameof(Status));
    }

}

public partial class ManualHardwareViewModel : ObservableObject
{
    private readonly MainConveyor _conveyor;
    private readonly MachineState _state;
    private readonly MachineController _machine;
    private volatile bool _active;
    private int _stateRefreshQueued;

    public bool IsHoming => _state.IsHoming;

    public ManualHardwareViewModel(
        MainConveyor conveyor,
        MachineState state,
        MachineController machine,
        IReadOnlyList<MotionHardwareSettings> hardware)
    {
        _conveyor = conveyor;
        _state = state;
        _machine = machine;
        Axes = hardware.SelectMany(section => section.AxisSignals.Select(axis =>
            new ManualAxisRow(section.Group, axis.Value, axis.Key, machine.GetMotionStatus(section.Group)))).ToArray();

        state.Changed += OnMachineStateChanged;
    }

    public ManualAxisRow[] Axes { get; }
    public HomeBlockReason HomeBlock => _machine.HomeBlock;
    public ManualConveyorStatus ConveyorStatus =>
        _state.ConveyorRunning
            ? ManualConveyorStatus.Running
            : ManualConveyorStatus.Stopped;

    [RelayCommand(CanExecute = nameof(CanRunConveyor))]
    private void RunConveyor() => _conveyor.RunMotor();

    private bool CanRunConveyor() => _state.ManualControlsEnabled;

    [RelayCommand]
    private void StopConveyor() => _conveyor.Stop();

    [RelayCommand(CanExecute = nameof(CanToggleServo))]
    private void ToggleServo(ManualAxisRow row)
    {
        var servoOn = row.Feedback.GetAxisState(row.Axis).ServoOn;
        _machine.SetServo(row.Group, row.Axis, !servoOn);
        row.RefreshState();
    }

    private bool CanToggleServo(ManualAxisRow? row) =>
        row is not null
        && _machine.CanSetServo(row.Group);

    [RelayCommand(CanExecute = nameof(CanHomeAxis))]
    private Task HomeAxisAsync(
        ManualAxisRow row,
        CancellationToken cancellationToken) =>
        _machine.HomeAxisAsync(row.Group, row.Axis, cancellationToken);

    private bool CanHomeAxis(ManualAxisRow? row) =>
        row is not null && _machine.CanHomeAxis(row.Group, row.Axis);

    [RelayCommand(CanExecute = nameof(CanStopHome))]
    private void StopHome() => HomeAxisCommand.Cancel();

    private bool CanStopHome() => IsHoming;

    public void Activate()
    {
        _active = true;
        _state.Refresh();
        RefreshRows();
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
            RefreshRows();
            RunConveyorCommand.NotifyCanExecuteChanged();
            ToggleServoCommand.NotifyCanExecuteChanged();
            HomeAxisCommand.NotifyCanExecuteChanged();
            StopHomeCommand.NotifyCanExecuteChanged();
        });
    }

    private void RefreshRows()
    {
        foreach (var row in Axes)
        {
            row.RefreshState();
        }
    }

}
