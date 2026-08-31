using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.DependencyInjection;

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
    IAxisMotion motion) : ObservableObject
{
    public MotionGroup Group { get; } = group;
    public MachineAxis Signal { get; } = signal;
    public MotionAxis Axis { get; } = axis;
    internal IAxisMotion Motion { get; } = motion;
    public double Position
    {
        get
        {
            var position = Motion.GetPosition();
            return Axis switch
            {
                MotionAxis.X => position.X,
                MotionAxis.Y => position.Y,
                MotionAxis.Z => position.Z,
                _ => throw new ArgumentOutOfRangeException(nameof(Axis)),
            };
        }
    }
    public bool ServoOn => AxisState.ServoOn;
    public bool IndividualHomeAvailable =>
        Group != MotionGroup.PcbSupply;
    public ManualAxisStatus Status
    {
        get
        {
            if (!Motion.IsReady)
            {
                return ManualAxisStatus.Unavailable;
            }

            var state = AxisState;
            if (state.Emergency)
            {
                return ManualAxisStatus.Emergency;
            }

            if (state.Alarm)
            {
                return ManualAxisStatus.Alarm;
            }

            if (state.NegativeLimit)
            {
                return ManualAxisStatus.NegativeLimit;
            }

            if (state.PositiveLimit)
            {
                return ManualAxisStatus.PositiveLimit;
            }

            if (!state.ServoOn)
            {
                return ManualAxisStatus.ServoOff;
            }

            if (!state.Homed)
            {
                return ManualAxisStatus.HomeRequired;
            }

            return !state.InPosition
                ? ManualAxisStatus.Moving
                : ManualAxisStatus.Ready;
        }
    }

    internal AxisState AxisState => Motion.GetAxisState(Axis);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(ServoOn));
        OnPropertyChanged(nameof(Status));
    }
}

public partial class ManualHardwareViewModel : ObservableObject
{
    private readonly MainConveyor _conveyor;
    private readonly MachineState _state;
    private readonly HomeSettings _home;
    private volatile bool _active;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunConveyorCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleServoCommand))]
    [NotifyCanExecuteChangedFor(nameof(HomeAxisCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopHomeCommand))]
    private bool _isHoming;

    public ManualHardwareViewModel(
        [FromKeyedServices(MotionGroup.PcbSupply)] IAxisMotion supply,
        [FromKeyedServices(MotionGroup.PcbPlacementHandler)] IXyMotion placement,
        [FromKeyedServices(MotionGroup.BoltFastening)] IXyMotion fastening,
        [FromKeyedServices(MotionGroup.InspectionGantry)] IXyMotion inspection,
        MainConveyor conveyor,
        MachineState state,
        HomeSettings home)
    {
        _conveyor = conveyor;
        _state = state;
        _home = home;
        Axes =
        [
            new(MotionGroup.PcbSupply, MachineAxis.PcbSupplyX, MotionAxis.X, supply),
            new(MotionGroup.PcbSupply, MachineAxis.PcbSupplyY, MotionAxis.Y, supply),
            new(MotionGroup.PcbSupply, MachineAxis.PcbSupplyZ, MotionAxis.Z, supply),
            new(MotionGroup.PcbPlacementHandler, MachineAxis.PcbPlacementHandlerX, MotionAxis.X, placement),
            new(MotionGroup.PcbPlacementHandler, MachineAxis.PcbPlacementHandlerY, MotionAxis.Y, placement),
            new(MotionGroup.PcbPlacementHandler, MachineAxis.PcbPlacementHandlerZ, MotionAxis.Z, placement),
            new(MotionGroup.BoltFastening, MachineAxis.BoltFasteningX, MotionAxis.X, fastening),
            new(MotionGroup.BoltFastening, MachineAxis.BoltFasteningY, MotionAxis.Y, fastening),
            new(MotionGroup.BoltFastening, MachineAxis.BoltFasteningZ, MotionAxis.Z, fastening),
            new(MotionGroup.InspectionGantry, MachineAxis.InspectionGantryX, MotionAxis.X, inspection),
            new(MotionGroup.InspectionGantry, MachineAxis.InspectionGantryY, MotionAxis.Y, inspection),
        ];

        state.Changed += OnMachineStateChanged;
        foreach (var motion in new IAxisMotion[]
                 {
                     supply,
                     placement,
                     fastening,
                     inspection,
                 })
        {
            motion.PositionChanged += (_, _, _) => OnPositionChanged(motion);
        }
    }

    public ManualAxisRow[] Axes { get; }
    public ManualConveyorStatus ConveyorStatus =>
        _state.ConveyorRunning
            ? ManualConveyorStatus.Running
            : ManualConveyorStatus.Stopped;

    [RelayCommand(CanExecute = nameof(CanRunConveyor))]
    private void RunConveyor() => _conveyor.RunMotor();

    private bool CanRunConveyor() =>
        _state.ManualControlsEnabled && !IsHoming;

    [RelayCommand]
    private void StopConveyor() => _conveyor.Stop();

    [RelayCommand(CanExecute = nameof(CanToggleServo))]
    private void ToggleServo(ManualAxisRow row)
    {
        row.Motion.SetServo(row.Axis, !row.ServoOn);
        row.Refresh();
    }

    private bool CanToggleServo(ManualAxisRow? row) =>
        row is not null
        && _state.ManualMode
        && _state.SafetyReady
        && !IsHoming
        && !_state.IsRunning
        && row.Motion.IsReady;

    [RelayCommand(CanExecute = nameof(CanHomeAxis))]
    private async Task HomeAxisAsync(
        ManualAxisRow row,
        CancellationToken cancellationToken)
    {
        IsHoming = true;
        try
        {
            await row.Motion.HomeAsync(
                row.Axis,
                row.Axis == MotionAxis.Z
                    ? _home.ZSpeed
                    : _home.HorizontalSpeed,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsHoming = false;
            RefreshRows();
            _state.Refresh();
        }
    }

    private bool CanHomeAxis(ManualAxisRow? row) =>
        row is { IndividualHomeAvailable: true }
        && _state.ManualMode
        && _state.SafetyReady
        && _state.DoorInterlockReady
        && !IsHoming
        && !_state.IsRunning
        && BufferAllowsHome(row.Signal)
        && row.Motion.IsReady
        && row.ServoOn;

    private bool BufferAllowsHome(MachineAxis axis) =>
        axis is not MachineAxis.PcbPlacementHandlerX
            and not MachineAxis.PcbPlacementHandlerY
            and not MachineAxis.PcbPlacementHandlerZ
        || !_state.SupplyInBufferArea;

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

    private void OnPositionChanged(IAxisMotion motion)
    {
        if (!_active)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_active)
            {
                return;
            }

            foreach (var row in Axes)
            {
                if (ReferenceEquals(row.Motion, motion))
                {
                    row.Refresh();
                }
            }
        });
    }

    private void OnMachineStateChanged()
    {
        if (!_active)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_active)
            {
                return;
            }

            if (!_state.AutomaticRunning
                && _state.ConveyorRunning
                && (!_state.ManualMode || !_state.SafetyReady))
            {
                _conveyor.Stop();
            }

            if (!_state.ManualMode || !_state.SafetyReady)
            {
                HomeAxisCommand.Cancel();
            }

            OnPropertyChanged(nameof(ConveyorStatus));
            RefreshRows();
            RunConveyorCommand.NotifyCanExecuteChanged();
            ToggleServoCommand.NotifyCanExecuteChanged();
            HomeAxisCommand.NotifyCanExecuteChanged();
        });
    }

    private void RefreshRows()
    {
        foreach (var row in Axes)
        {
            row.Refresh();
        }
    }
}
