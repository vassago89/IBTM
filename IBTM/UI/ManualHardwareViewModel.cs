using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly BoltFasteningGantry _fastening;
    private readonly InspectionGantry _inspection;
    private readonly MainConveyor _conveyor;
    private readonly MachineState _state;
    private readonly MachineController _machine;
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
        BoltFasteningGantry fastening,
        InspectionGantry inspection,
        MainConveyor conveyor,
        MachineState state,
        MachineController machine,
        HomeSettings home,
        IReadOnlyList<MotionHardwareSettings> hardware)
    {
        _supply = supply;
        _placement = placement;
        _fastening = fastening;
        _inspection = inspection;
        _conveyor = conveyor;
        _state = state;
        _machine = machine;
        _home = home;
        var feedbacks = new Dictionary<MotionGroup, IMotionFeedback>
        {
            [MotionGroup.PcbSupply] = supply.Feedback,
            [MotionGroup.PcbPlacementHandler] = placement.Feedback,
            [MotionGroup.BoltFastening] = fastening.Feedback,
            [MotionGroup.InspectionGantry] = inspection.Feedback,
        };
        Axes = hardware.SelectMany(section => section.AxisSignals.Select(axis =>
            new ManualAxisRow(section.Group, axis.Value, axis.Key, feedbacks[section.Group]))).ToArray();

        state.Changed += OnMachineStateChanged;
        foreach (var (group, feedback) in feedbacks)
        {
            feedback.PositionChanged += (x, y, z) =>
                OnPositionChanged(group, x, y, z);
        }
    }

    public ManualAxisRow[] Axes { get; }
    public HomeBlockReason HomeBlock => _machine.HomeBlock;
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
        if (!CanHomeAxis(row)) return;
        IsHoming = true;
        void StopWhenHomeUnavailable()
        {
            if (!HomeConditionsReady(row))
            {
                HomeAxisCommand.Cancel();
            }
        }

        _state.Changed += StopWhenHomeUnavailable;
        try
        {
            _state.SetHoming(true);
            if (!await HomeOwnerAxisAsync(row, cancellationToken)
                && !cancellationToken.IsCancellationRequested)
            {
                _state.SetError(MachineAlarm.HomeFailed);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
            when (exception is IoTimeoutException or MotionException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _state.SetError(MachineAlarm.HomeFailed, exception);
            }
        }
        finally
        {
            _state.Changed -= StopWhenHomeUnavailable;
            IsHoming = false;
            _state.SetHoming(false);
            RefreshRows();
            _state.Refresh();
        }
    }

    private bool CanHomeAxis(ManualAxisRow? row) =>
        row is { IndividualHomeAvailable: true }
        && !IsHoming
        && !_state.IsRunning
        && (row.Group != MotionGroup.PcbPlacementHandler || !_state.SupplyInBufferArea)
        && HomeConditionsReady(row);

    private bool HomeConditionsReady(ManualAxisRow row) =>
        _state.ManualMode
        && _state.SafetyReady
        && _state.DoorInterlockReady
        && !_state.IsError
        && _machine.GetHomeBlock(row.Group) == HomeBlockReason.None
        && HomeHardwareReady(row);

    private bool HomeHardwareReady(ManualAxisRow row)
    {
        if (!row.Feedback.IsReady || !_state.ServoMainContactorOn)
        {
            return false;
        }

        foreach (var axis in row.Feedback.Axes)
        {
            if (axis != row.Axis && axis != MotionAxis.Z) continue;
            var state = row.Feedback.GetAxisState(axis);
            if (!state.ServoOn || state.Alarm || state.Emergency) return false;
        }

        return true;
    }

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
            OnPropertyChanged(nameof(HomeBlock));
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
