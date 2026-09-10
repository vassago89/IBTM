using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    [Description("Hold to jog")]
    Jog,
    [Description("Step")]
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
    [Description("Supply is inside the buffer area.")]
    SupplyInBuffer,
    [Description("Placement is inside the buffer area.")]
    PlacementInBuffer,
    [Description("Raise the NG pickup before moving XY.")]
    RaiseNgPickup,
    [Description("Raise the placement handler before moving X/Y.")]
    RaisePlacementCylinders,
    [Description("Jog/Step adjust one axis at the current height. Raise both heads before moving to a saved position.")]
    BoltAdjustment,
    [Description("Move to Safe Z before moving X/Y.")]
    SafeZRequired,
    [Description("Inside buffer: Y and Z moves are disabled.")]
    SupplyInBufferRestricted,
}

public abstract partial class TeachingMotionViewModel(
    MachineState state,
    MachineController machine,
    MachineStore store,
    IReadOnlyDictionary<HardwareArea, IoStatus[]> ioGroups,
    IReadOnlyDictionary<HardwareArea, IReadOnlyDictionary<OutputIo, TeachingOutput>> teachingOutputs) : ObservableObject
{
    private CancellationTokenSource _viewCancellation = new();
    private readonly Dictionary<HardwareArea, TeachingIoGroup[]> _teachingIoGroups = [];
    private int _manualCommandRefreshQueued;

    [ObservableProperty]
    private double _jogSpeed = 10.0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StepCommand))]
    private double _stepDistance = 0.1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualSpeedLabel))]
    private TeachingMoveMode _moveMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TeachCurrentPositionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveToPointCommand))]
    [NotifyPropertyChangedFor(nameof(TeachingIoGroups))]
    private TeachingPoint? _selectedPoint;

    [ObservableProperty]
    private string? _saveError;

    public TeachingMoveMode[] MoveModes { get; } = Enum.GetValues<TeachingMoveMode>();

    public string ManualSpeedLabel
    {
        get
        {
            return MoveMode == TeachingMoveMode.Step ? "Step speed" : "Jog speed";
        }
    }

    public ManualControlBlock ManualBlock
    {
        get
        {
            return CanEditTeaching
                ? ManualControlBlock.None
                : state.Display.AutoMode ? ManualControlBlock.AutoMode : ManualControlBlock.Busy;
        }
    }

    public bool CanEditTeaching
    {
        get
        {
            return state.Display.SetupEditingEnabled;
        }
    }

    public abstract TeachingMotionHint MotionHint { get; }
    public abstract TeachingSaveBehavior SaveBehavior { get; }

    public IReadOnlyList<TeachingIoGroup> TeachingIoGroups
    {
        get
        {
            if (!_teachingIoGroups.TryGetValue(ActiveTeachingUnit, out var groups))
            {
                groups = ioGroups[ActiveTeachingUnit].Select(
                    io =>
                        new TeachingIoGroup(
                            io,
                            TeachingOutputs,
                            SetOutputOnCommand,
                            SetOutputOffCommand,
                            SetOutputOnCancelCommand))
                    .ToArray();
                _teachingIoGroups.Add(ActiveTeachingUnit, groups);
            }

            return groups;
        }
    }

    public IReadOnlyDictionary<OutputIo, TeachingOutput> TeachingOutputs
    {
        get
        {
            return teachingOutputs[ActiveTeachingUnit];
        }
    }

    public MotionStatus Motion
    {
        get
        {
            return state.GetMotionStatus(ActiveMotionGroup);
        }
    }

    protected MachineController Machine { get; } = machine;

    public abstract MotionGroup ActiveMotionGroup { get; }
    public abstract HardwareArea ActiveTeachingUnit { get; }

    protected bool PositionUpdatesActive { get; private set; }

    protected CancellationToken ViewCancellation
    {
        get
        {
            return _viewCancellation.Token;
        }
    }

    protected abstract IReadOnlyList<TeachingPoint> CurrentPoints { get; }
    partial void OnSelectedPointChanged(TeachingPoint? oldValue, TeachingPoint? newValue)
    {
        CancelTeaching();
        NotifyPointSelectionCommands();
        OnPropertyChanged(nameof(SaveBehavior));
        OnTeachingPointChanged(oldValue, newValue);
    }

    protected abstract void OnTeachingPointChanged(TeachingPoint? oldValue, TeachingPoint? newValue);

    [RelayCommand(CanExecute = nameof(CanTeachCurrentPosition))]
    private Task TeachCurrentPositionAsync(CancellationToken cancellationToken)
    {
        return Machine.RunTeachingEditAsync(
            async token =>
            {
                if (SelectedPoint is not { Position.Mode: not TeachMode.Image, Position.CanTeach: true } point
                    || !Motion.Feedback.IsReady)
                    return;
                var current = Motion.Feedback.GetPosition();
                point.Teach(current.X, current.Y, current.Z);
                if (point.Position.Storage == TeachingStorage.Buffer)
                    return;

                point.Apply();
                RefreshPointPositions();
                if (point.Position.Storage == TeachingStorage.Machine
                    && !await SaveSettingsAsync(token, point.Position.Setting!))
                    return;

                token.ThrowIfCancellationRequested();
                OnPointTaught(point);
                NotifyManualTeachingCommands();
            },
            cancellationToken,
            ViewCancellation);
    }

    private bool CanTeachCurrentPosition()
    {
        return SelectedPoint is { Position.Mode: not TeachMode.Image, Position.CanTeach: true }
            && CanEditTeaching
            && Motion.Axes.Values.All(axis => axis.State is not null);
    }

    protected abstract void RefreshPointPositions();
    protected virtual void OnPointTaught(TeachingPoint point)
    {
    }

    // Concrete views keep command execution and device selection together.
    [RelayCommand(CanExecute = nameof(CanMoveToPoint))]
    protected abstract Task MoveToPointAsync(CancellationToken cancellationToken);

    protected abstract bool CanMoveToPoint();

    protected async Task<bool> SaveSettingsAsync(
        CancellationToken cancellationToken,
        params Setting[] settings)
    {
        SaveError = null;
        try
        {
            await Task.Run(() => store.SaveSettings(settings, cancellationToken), cancellationToken);
            System.Diagnostics.Trace.TraceInformation(
                "Teaching settings saved: {0}.",
                ActiveMotionGroup);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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

    private int CurrentPointIndex
    {
        get
        {
            for (var index = 0; index < CurrentPoints.Count; index++)
                if (CurrentPoints[index] == SelectedPoint)
                    return index;
            return -1;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectPreviousPoint))]
    private void SelectPreviousPoint()
    {
        SelectedPoint = CurrentPoints[CurrentPointIndex - 1];
    }

    [RelayCommand(CanExecute = nameof(CanSelectNextPoint))]
    private void SelectNextPoint()
    {
        SelectedPoint = CurrentPoints[CurrentPointIndex + 1];
    }

    private bool CanSelectPreviousPoint()
    {
        return CurrentPointIndex > 0;
    }

    private bool CanSelectNextPoint()
    {
        return CurrentPointIndex < CurrentPoints.Count - 1;
    }

    protected void NotifyPointSelectionCommands()
    {
        SelectPreviousPointCommand.NotifyCanExecuteChanged();
        SelectNextPointCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSetOutput), IncludeCancelCommand = true)]
    private Task SetOutputOnAsync(TeachingOutput output, CancellationToken cancellationToken)
    {
        return SetOutputAsync(output, true, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanSetOutput))]
    private Task SetOutputOffAsync(TeachingOutput output, CancellationToken cancellationToken)
    {
        return SetOutputAsync(output, false, cancellationToken);
    }

    private bool CanSetOutput(TeachingOutput? output)
    {
        return output is not null
            && TeachingOutputs.ContainsKey(output.Signal)
            && Machine.CanSetTeachingOutput(output, live: false);
    }

    private async Task SetOutputAsync(
        TeachingOutput output,
        bool value,
        CancellationToken cancellationToken)
    {
        try
        {
            await Machine.RunTeachingOutputAsync(output, value, cancellationToken, ViewCancellation);
        }
        finally
        {
            NotifyManualTeachingCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveDirection))]
    protected abstract Task JogAsync(TeachingDirection direction, CancellationToken cancellationToken);

    [RelayCommand(CanExecute = nameof(CanStep))]
    protected abstract Task StepAsync(TeachingDirection direction, CancellationToken cancellationToken);

    protected (MotionAxis Axis, double Position) StepTarget(
        TeachingDirection direction,
        (double X, double Y, double Z) current)
    {
        var (axis, sign) = Resolve(direction);
        var position = axis switch
        {
            MotionAxis.X => current.X,
            MotionAxis.Y => current.Y,
            MotionAxis.Z => current.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
        return (axis, position + sign * StepDistance);
    }

    private bool CanStep(TeachingDirection direction)
    {
        if (!CanMoveDirection(direction))
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
        var target = current.Value + sign * StepDistance;
        return Motion.Feedback.GetRange(axis) is not { } range
            || target >= range.Minimum
            && target <= range.Maximum;
    }

    protected static (MotionAxis Axis, int Sign) Resolve(TeachingDirection direction)
    {
        return direction switch
        {
            TeachingDirection.XMinus => (MotionAxis.X, -1),
            TeachingDirection.XPlus => (MotionAxis.X, 1),
            TeachingDirection.YMinus => (MotionAxis.Y, -1),
            TeachingDirection.YPlus => (MotionAxis.Y, 1),
            TeachingDirection.ZMinus => (MotionAxis.Z, -1),
            TeachingDirection.ZPlus => (MotionAxis.Z, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
    }

    private bool CanMoveDirection(TeachingDirection direction)
    {
        return CanJog(Resolve(direction).Axis);
    }

    [RelayCommand]
    private void JogStop()
    {
        CancelTeaching();
    }

    private bool CanJogZ()
    {
        return CanJog(MotionAxis.Z);
    }

    protected abstract bool CanJog(MotionAxis axis);
    protected abstract void NotifyManualTeachingCommands();

    [RelayCommand(CanExecute = nameof(CanHomeAxis))]
    private async Task HomeAxisAsync(MotionAxis axis, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            ViewCancellation);
        await Machine.HomeAxisAsync(ActiveMotionGroup, axis, cancellation.Token);
    }

    private bool CanHomeAxis(MotionAxis axis)
    {
        return Motion.Axes.ContainsKey(axis)
            && Machine.CanHomeAxis(ActiveMotionGroup, axis, live: false);
    }

    [RelayCommand(CanExecute = nameof(CanJogZ))]
    protected abstract Task MoveToHorizontalZAsync(CancellationToken cancellationToken);

    protected void CancelTeaching()
    {
        var cancellation = _viewCancellation;
        _viewCancellation = new CancellationTokenSource();
        cancellation.Cancel();
        cancellation.Dispose();
    }

    protected void NotifyMotionCommands()
    {
        HomeAxisCommand.NotifyCanExecuteChanged();
        JogCommand.NotifyCanExecuteChanged();
        StepCommand.NotifyCanExecuteChanged();
        MoveToHorizontalZCommand.NotifyCanExecuteChanged();
        SetOutputOnCommand.NotifyCanExecuteChanged();
        SetOutputOffCommand.NotifyCanExecuteChanged();
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        MoveToPointCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ManualBlock));
        OnPropertyChanged(nameof(CanEditTeaching));
        OnPropertyChanged(nameof(MotionHint));
    }

    protected void ActivatePositionUpdates()
    {
        PositionUpdatesActive = true;
        OnPropertyChanged(nameof(Motion));
    }

    public virtual void Deactivate()
    {
        PositionUpdatesActive = false;
        CancelTeaching();
    }

    protected void QueueManualCommandRefresh()
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
