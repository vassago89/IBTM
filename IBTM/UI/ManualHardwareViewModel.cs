using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

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
    IMotionFeedback feedback) : ObservableObject
{
    private double _position = Coordinate(feedback.GetPosition(), axis);
    private AxisState _axisState = feedback.GetAxisState(axis);

    public MotionGroup Group { get; } = group;
    public MachineAxis Signal { get; } = signal;
    public MotionAxis Axis { get; } = axis;
    internal IMotionFeedback Feedback { get; } = feedback;
    public double Position => Volatile.Read(ref _position);
    public bool ServoOn => _axisState.ServoOn;
    public bool IndividualHomeAvailable =>
        Group != MotionGroup.PcbSupply;
    public ManualAxisStatus Status
    {
        get
        {
            if (!Feedback.IsReady)
            {
                return ManualAxisStatus.Unavailable;
            }

            var state = _axisState;
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

    internal void SetPosition(double x, double y, double z) =>
        Volatile.Write(
            ref _position,
            Coordinate((x, y, z), Axis));

    internal void RefreshPosition() =>
        OnPropertyChanged(nameof(Position));

    internal void RefreshState()
    {
        _axisState = Feedback.GetAxisState(Axis);
        OnPropertyChanged(nameof(ServoOn));
        OnPropertyChanged(nameof(Status));
    }

    private static double Coordinate(
        (double X, double Y, double Z) position,
        MotionAxis axis) => axis switch
        {
            MotionAxis.X => position.X,
            MotionAxis.Y => position.Y,
            MotionAxis.Z => position.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
}

public partial class ManualHardwareViewModel : ObservableObject
{
    private readonly PcbSupplyHandler _supply;
    private readonly PcbPlacementHandler _placement;
    private readonly BoltFasteningStation _fastening;
    private readonly InspectionGantry _inspection;
    private readonly MainConveyor _conveyor;
    private readonly MachineState _state;
    private readonly HomeSettings _home;
    private volatile bool _active;
    private int _pendingPositionGroups;
    private int _positionRefreshQueued;
    private int _stateRefreshQueued;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunConveyorCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleServoCommand))]
    [NotifyCanExecuteChangedFor(nameof(HomeAxisCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopHomeCommand))]
    private bool _isHoming;

    public ManualHardwareViewModel(
        PcbSupplyHandler supply,
        PcbPlacementHandler placement,
        BoltFasteningStation fastening,
        InspectionGantry inspection,
        MainConveyor conveyor,
        MachineState state,
        HomeSettings home)
    {
        _supply = supply;
        _placement = placement;
        _fastening = fastening;
        _inspection = inspection;
        _conveyor = conveyor;
        _state = state;
        _home = home;
        Axes =
        [
            new(MotionGroup.PcbSupply, MachineAxis.PcbSupplyX, MotionAxis.X, supply.Feedback),
            new(MotionGroup.PcbSupply, MachineAxis.PcbSupplyY, MotionAxis.Y, supply.Feedback),
            new(MotionGroup.PcbSupply, MachineAxis.PcbSupplyZ, MotionAxis.Z, supply.Feedback),
            new(MotionGroup.PcbPlacementHandler, MachineAxis.PcbPlacementHandlerX, MotionAxis.X, placement.Feedback),
            new(MotionGroup.PcbPlacementHandler, MachineAxis.PcbPlacementHandlerY, MotionAxis.Y, placement.Feedback),
            new(MotionGroup.PcbPlacementHandler, MachineAxis.PcbPlacementHandlerZ, MotionAxis.Z, placement.Feedback),
            new(MotionGroup.BoltFastening, MachineAxis.BoltFasteningX, MotionAxis.X, fastening.Feedback),
            new(MotionGroup.BoltFastening, MachineAxis.BoltFasteningY, MotionAxis.Y, fastening.Feedback),
            new(MotionGroup.BoltFastening, MachineAxis.BoltFasteningZ, MotionAxis.Z, fastening.Feedback),
            new(MotionGroup.InspectionGantry, MachineAxis.InspectionGantryX, MotionAxis.X, inspection.Feedback),
            new(MotionGroup.InspectionGantry, MachineAxis.InspectionGantryY, MotionAxis.Y, inspection.Feedback),
        ];

        state.Changed += OnMachineStateChanged;
        foreach (var (group, feedback) in new[]
                 {
                     (MotionGroup.PcbSupply, supply.Feedback),
                     (MotionGroup.PcbPlacementHandler, placement.Feedback),
                     (MotionGroup.BoltFastening, fastening.Feedback),
                     (MotionGroup.InspectionGantry, inspection.Feedback),
                 })
        {
            feedback.PositionChanged += (x, y, z) =>
                OnPositionChanged(group, x, y, z);
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
        var servoOn = row.Feedback.GetAxisState(row.Axis).ServoOn;
        SetServo(row.Group, row.Axis, !servoOn);
        row.RefreshState();
    }

    private bool CanToggleServo(ManualAxisRow? row) =>
        row is not null
        && _state.ManualMode
        && _state.SafetyReady
        && !IsHoming
        && !_state.IsRunning
        && row.Feedback.IsReady;

    [RelayCommand(CanExecute = nameof(CanHomeAxis))]
    private async Task HomeAxisAsync(
        ManualAxisRow row,
        CancellationToken cancellationToken)
    {
        IsHoming = true;
        try
        {
            await HomeOwnerAxisAsync(row, cancellationToken);
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
        && row.Feedback.IsReady
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

    private void OnPositionChanged(
        MotionGroup group,
        double x,
        double y,
        double z)
    {
        if (!_active)
        {
            return;
        }

        foreach (var row in Axes)
        {
            if (row.Group != group)
            {
                continue;
            }

            row.SetPosition(x, y, z);
        }

        Interlocked.Or(ref _pendingPositionGroups, 1 << (int)group);
        if (Interlocked.Exchange(ref _positionRefreshQueued, 1) != 0)
        {
            return;
        }

        Application.Current.Dispatcher.InvokeAsync(
            () =>
            {
                Interlocked.Exchange(ref _positionRefreshQueued, 0);
                if (!_active)
                {
                    return;
                }

                var pending = Interlocked.Exchange(
                    ref _pendingPositionGroups,
                    0);
                foreach (var row in Axes)
                {
                    if ((pending & (1 << (int)row.Group)) != 0)
                    {
                        row.RefreshPosition();
                    }
                }
            },
            DispatcherPriority.Background);
    }

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
        IMotionFeedback? feedback = null;
        (double X, double Y, double Z) position = default;
        foreach (var row in Axes)
        {
            if (!ReferenceEquals(feedback, row.Feedback))
            {
                feedback = row.Feedback;
                position = feedback.GetPosition();
            }

            row.SetPosition(position.X, position.Y, position.Z);
            row.RefreshPosition();
            row.RefreshState();
        }
    }

    private void SetServo(
        MotionGroup group,
        MotionAxis axis,
        bool on)
    {
        switch (group)
        {
            case MotionGroup.PcbSupply:
                _supply.SetServo(axis, on);
                break;
            case MotionGroup.PcbPlacementHandler:
                _placement.SetServo(axis, on);
                break;
            case MotionGroup.BoltFastening:
                _fastening.SetServo(axis, on);
                break;
            case MotionGroup.InspectionGantry:
                _inspection.SetServo(axis, on);
                break;
        }
    }

    private Task<bool> HomeOwnerAxisAsync(
        ManualAxisRow row,
        CancellationToken cancellationToken)
    {
        var velocity = row.Axis == MotionAxis.Z
            ? _home.ZSpeed
            : _home.HorizontalSpeed;
        return row.Group switch
        {
            MotionGroup.PcbPlacementHandler =>
                _placement.HomeAxisAsync(
                    row.Axis,
                    velocity,
                    cancellationToken),
            MotionGroup.BoltFastening =>
                _fastening.HomeAxisAsync(
                    row.Axis,
                    velocity,
                    cancellationToken),
            MotionGroup.InspectionGantry =>
                _inspection.HomeAxisAsync(
                    row.Axis,
                    velocity,
                    cancellationToken),
            _ => Task.FromResult(false),
        };
    }
}
