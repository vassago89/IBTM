using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.UI;

public enum TeachingMoveMode
{
    [Description("Jog · hold to move")]
    Jog,
    [Description("Step · move a set distance")]
    Step,
}

public enum TeachingDirection
{
    [Description("X−")]
    XMinus,
    [Description("X+")]
    XPlus,
    [Description("Y−")]
    YMinus,
    [Description("Y+")]
    YPlus,
    [Description("Z−")]
    ZMinus,
    [Description("Z+")]
    ZPlus,
}

public enum TeachingMotionHint
{
    [Description("")]
    None,
    [Description("This unit is disabled in Settings.")]
    UnitDisabled,
    [Description("Raise the NG pickup before moving XY.")]
    RaiseNgPickup,
    [Description("Z Jog/Step is available with the handler lowered. Raise the handler before X/Y, Move to Position or Move Z to Standby Height.")]
    RaisePlacementCylinders,
    [Description("Jog/Step adjust one axis at the current height. Raise both heads before moving to a teaching position.")]
    BoltAdjustment,
    [Description("Home this unit before jogging or moving to a teaching position.")]
    HomeRequired,
    [Description("Turn on this unit's axis servos before moving.")]
    ServoOff,
    [Description("Clear this unit's axis alarm or emergency signal before moving.")]
    AxisFault,
    [Description("Motion feedback is unavailable for this unit.")]
    MotionUnavailable,
    [Description("Record Carrier Pickup (S3) X/Y before moving to a carrier.")]
    NgPickupPositionRequired,
}

public partial class TeachingViewModel
{
    private readonly MachineStore _store;
    private readonly IReadOnlyDictionary<HardwareArea, IoStatus[]> _ioGroups;
    private readonly IReadOnlyDictionary<HardwareArea, IReadOnlyDictionary<OutputIo, TeachingOutput>> _teachingOutputs;
    private CancellationTokenSource _viewCancellation;
    private readonly Dictionary<HardwareArea, TeachingIoGroup[]> _teachingIoGroups;
    private int _manualCommandRefreshQueued;

    [ObservableProperty]
    public partial double JogSpeed { get; set; } = 10.0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StepCommand))]
    public partial double StepDistance { get; set; } = 0.1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualSpeedLabel))]
    public partial TeachingMoveMode MoveMode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToPointCommand))]
    [NotifyPropertyChangedFor(nameof(TeachingIoGroups))]
    public partial TeachingPoint? SelectedPoint { get; set; }

    [ObservableProperty]
    public partial string? SaveError { get; set; }

    public TeachingMoveMode[] MoveModes { get; }

    public string ManualSpeedLabel => MoveMode == TeachingMoveMode.Step ? "Step speed" : "Jog speed";

    public ManualControlBlock ManualBlock
    {
        get
        {
            switch (true)
            {
                case true when IsTeachingEditAllowed:
                    return ManualControlBlock.None;
                case true when State.AutoMode:
                    return ManualControlBlock.AutoMode;
                default:
                    return ManualControlBlock.Busy;
            }
        }
    }

    public bool IsTeachingEditAllowed => State.SetupEditingEnabled;

    public IReadOnlyList<TeachingIoGroup> TeachingIoGroups
    {
        get
        {
            if (!_teachingIoGroups.TryGetValue(SelectedTeachingUnit, out var groups))
            {
                groups = _ioGroups[SelectedTeachingUnit].Select(
                    io =>
                        new TeachingIoGroup(
                            io,
                            _teachingOutputs[SelectedTeachingUnit],
                            Machine))
                    .ToArray();
                foreach (var row in groups.SelectMany(group => group.Outputs))
                    row.ViewCancellation = ViewCancellation;
                _teachingIoGroups.Add(SelectedTeachingUnit, groups);
            }

            return groups;
        }
    }

    private IAsyncRelayCommand[] OutputCommands
    {
        get
        {
            return _teachingIoGroups.Values.SelectMany(groups => groups)
                .SelectMany(group => group.Outputs)
                .Select(row => row.ToggleOutputCommand)
                .ToArray();
        }
    }

    public MotionStatus Motion => State.GetMotionStatus(ActiveMotionGroup);

    private MachineController Machine { get; }
    private MachineState State { get; }
    private OperationCancellation Operations { get; }

    private bool PositionUpdatesActive { get; set; }

    private CancellationToken ViewCancellation => _viewCancellation.Token;

    private int CurrentPointIndex
    {
        get
        {
            for (var index = 0; index < FilteredPoints.Count; index++)
                if (FilteredPoints[index] == SelectedPoint)
                    return index;
            return -1;
        }
    }

    public IAsyncRelayCommand TeachCurrentPositionCommand { get; }

    private async Task TeachCurrentPositionAsync(CancellationToken cancellationToken)
    {
        if (SelectedPoint?.Position.Mode == TeachMode.Image)
        {
            if (IsRecordImagePositionAllowed)
                await RecordImagePositionAsync(cancellationToken);
            return;
        }
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (!(State.SetupEditingEnabled))
                return;
            using var operation = Machine.BeginManualOperation(
                () => State.ManualMode,
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            switch (SelectedPoint)
            {
                case { Position.Mode: not TeachMode.Image, Position.IsTeachAllowed: true } point
                    when Motion.Feedback.IsReady && IsReadTeachingPositionAllowed(point, live: true):
                    SaveError = null;
                    var current = Motion.Feedback.GetPosition();
                    point.Teach(current.X, current.Y, current.Z);
                    RefreshPointPositions();
                    if (point.Position.Storage == TeachingStorage.Machine
                        && !await SaveSettingsAsync(operation.Token, point.Position.Setting!))
                        return;
                    operation.Token.ThrowIfCancellationRequested();
                    OnPointTaught(point);
                    NotifyManualTeachingCommands();
                    break;
                case { Position.Mode: not TeachMode.Image, Position.IsTeachAllowed: true }:
                    SaveError = "Home the axes used by this teaching position and wait for them to stop before teaching.";
                    break;
            }
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(MachineAlarm.IoCommunication, exception);
        }
    }

    private bool IsTeachCurrentPositionAllowed
    {
        get
        {
            if (SelectedPoint?.Position.Mode == TeachMode.Image)
                return IsRecordImagePositionAllowed;
            return SelectedPoint is { Position.Mode: not TeachMode.Image, Position.IsTeachAllowed: true } point
                && IsTeachingEditAllowed
                && IsReadTeachingPositionAllowed(point, live: false);
        }
    }

    private bool IsReadTeachingPositionAllowed(TeachingPoint point, bool live)
    {
        MotionAxis[] axes = point.Position.Mode switch
        {
            TeachMode.Full => [MotionAxis.X, MotionAxis.Y, MotionAxis.Z],
            TeachMode.XYOnly => [MotionAxis.X, MotionAxis.Y],
            TeachMode.XOnly => [MotionAxis.X],
            TeachMode.YOnly => [MotionAxis.Y],
            TeachMode.ZOnly => [MotionAxis.Z],
            _ => [],
        };
        return axes.Length > 0 && axes.All(axis =>
            (live ? Motion.Feedback.GetAxisState(axis) : Motion.Axes[axis].State)
                is { Homed: true, InMotion: false });
    }

    private async Task<bool> SaveSettingsAsync(
        CancellationToken cancellationToken,
        params Setting[] settings)
    {
        SaveError = null;
        try
        {
            await _store.SaveSettingsAsync(settings, cancellationToken);
            System.Diagnostics.Trace.TraceInformation(
                "Teaching settings saved: {0}.",
                ActiveMotionGroup);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SaveError = "Teaching save cancelled. Values have not been saved.";
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Teaching settings save failed: {0}. {1}",
                ActiveMotionGroup,
                exception);
            SaveError = $"Teaching values were not saved: {exception.GetBaseException().Message}";
            return false;
        }
    }

    public IRelayCommand SelectPreviousPointCommand { get; }

    private void SelectPreviousPoint()
    {
        SelectedPoint = FilteredPoints[CurrentPointIndex - 1];
    }

    public IRelayCommand SelectNextPointCommand { get; }

    private void SelectNextPoint()
    {
        SelectedPoint = FilteredPoints[CurrentPointIndex + 1];
    }

    private bool IsSelectPreviousPointAllowed => CurrentPointIndex > 0;

    private bool IsSelectNextPointAllowed => CurrentPointIndex < FilteredPoints.Count - 1;

    private void NotifyPointSelectionCommands()
    {
        SelectPreviousPointCommand.NotifyCanExecuteChanged();
        SelectNextPointCommand.NotifyCanExecuteChanged();
    }

    private bool IsStepAllowed(TeachingDirection direction)
    {
        if (!IsMoveDirectionAllowed(direction))
            return false;
        var position = Motion.Position;
        var (axis, sign) = Resolve(direction);
        var current = axis switch
        {
            MotionAxis.X => position.X,
            MotionAxis.Y => position.Y,
            MotionAxis.Z => position.Z,
            _ => null,
        };
        if (current is null)
            return false;
        return double.IsFinite(current.Value + sign * StepDistance);
    }

    private static (MotionAxis Axis, int Sign) Resolve(TeachingDirection direction)
    {
        switch (direction)
        {
            case TeachingDirection.XMinus:
                return (MotionAxis.X, -1);
            case TeachingDirection.XPlus:
                return (MotionAxis.X, 1);
            case TeachingDirection.YMinus:
                return (MotionAxis.Y, -1);
            case TeachingDirection.YPlus:
                return (MotionAxis.Y, 1);
            case TeachingDirection.ZMinus:
                return (MotionAxis.Z, -1);
            case TeachingDirection.ZPlus:
                return (MotionAxis.Z, 1);
            default:
                throw new ArgumentOutOfRangeException(nameof(direction));
        }
    }

    private bool IsMoveDirectionAllowed(TeachingDirection direction)
    {
        return IsJogAllowed(Resolve(direction).Axis);
    }

    public IRelayCommand JogStopCommand { get; }

    private void JogStop()
    {
        CancelTeaching();
    }

    private bool IsMoveToHorizontalZAllowed => IsJogAllowed(MotionAxis.Z)
        && (ActiveMotionGroup != MotionGroup.PcbPlacementHandler || _pcbPlacement.HandlerRaised);

    public IAsyncRelayCommand HomeCommand { get; }

    private async Task HomeAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            ViewCancellation);
        var group = ActiveMotionGroup;
        await Machine.HomeAsync(group, cancellation.Token);
    }

    private bool IsHomeAllowed => Motion.Feedback.Axes.All(axis => Machine.IsHomeAxisAllowed(ActiveMotionGroup, axis));

    private void CancelTeaching(bool reportDeviceFailure = true)
    {
        var cancellation = _viewCancellation;
        _viewCancellation = new CancellationTokenSource();
        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception) when (reportDeviceFailure
            && (exception is IOException or MotionException
                || exception is AggregateException aggregate
                    && aggregate.Flatten().InnerExceptions.Any(error => error is IOException or MotionException)))
        {
            if (!State.IsError)
                State.SetError(MachineAlarm.StopFailed, exception);
            else
                System.Diagnostics.Trace.TraceError("Teaching STOP also failed. {0}", exception);
        }
        finally
        {
            cancellation.Dispose();
            foreach (var row in TeachingIoGroups.SelectMany(group => group.Outputs))
            {
                row.ViewCancellation = ViewCancellation;
                row.ToggleOutputCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void NotifyMotionCommands()
    {
        HomeCommand.NotifyCanExecuteChanged();
        JogCommand.NotifyCanExecuteChanged();
        StepCommand.NotifyCanExecuteChanged();
        MoveToHorizontalZCommand.NotifyCanExecuteChanged();
        foreach (var row in TeachingIoGroups.SelectMany(group => group.Outputs))
            row.ToggleOutputCommand.NotifyCanExecuteChanged();
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        MoveToPointCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ManualBlock));
        OnPropertyChanged(nameof(IsTeachingEditAllowed));
        OnPropertyChanged(nameof(MotionHint));
    }

    private void OnMachineStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        QueueManualCommandRefresh();
    }

    private void OnTeachingMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MotionStatus.Position) or nameof(AxisStatus.State))
            QueueManualCommandRefresh();
    }

    private void SubscribeMotionChanges()
    {
        Motion.PropertyChanged += OnTeachingMotionChanged;
        foreach (var axis in Motion.Axes.Values)
            axis.PropertyChanged += OnTeachingMotionChanged;
    }

    private void UnsubscribeMotionChanges()
    {
        Motion.PropertyChanged -= OnTeachingMotionChanged;
        foreach (var axis in Motion.Axes.Values)
            axis.PropertyChanged -= OnTeachingMotionChanged;
    }

    private void QueueManualCommandRefresh()
    {
        if (!PositionUpdatesActive
            || Interlocked.Exchange(ref _manualCommandRefreshQueued, 1) != 0)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(
            () =>
            {
                Interlocked.Exchange(ref _manualCommandRefreshQueued, 0);
                if (PositionUpdatesActive)
                {
                    NotifyManualTeachingCommands();
                }
            });
    }
}
