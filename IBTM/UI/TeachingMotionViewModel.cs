using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    [Description("X−")] XMinus,
    [Description("X+")] XPlus,
    [Description("Y−")] YMinus,
    [Description("Y+")] YPlus,
    [Description("Z−")] ZMinus,
    [Description("Z+")] ZPlus,
}

public enum TeachingMotionHint
{
    [Description("")] None,
    [Description("This unit is disabled in Settings.")] UnitDisabled,
    [Description("Supply is inside the buffer area.")] SupplyInBuffer,
    [Description("Placement is inside the buffer area.")] PlacementInBuffer,
    [Description("Raise the NG pickup before moving XY.")] RaiseNgPickup,
    [Description("Raise the placement handler before moving X/Y.")] RaisePlacementCylinders,
    [Description("Jog/Step adjust one axis at the current height. Raise both heads before moving to a saved position.")] BoltAdjustment,
    [Description("Move to Safe Z before moving X/Y.")] SafeZRequired,
    [Description("Inside buffer: Y and Z moves are disabled.")] SupplyInBufferRestricted,
}

public abstract partial class TeachingMotionViewModel(
    MachineState state,
    MachineController machine,
    MachineStore store,
    IReadOnlyDictionary<MotionGroup, IoStatus[]> ioGroups,
    IReadOnlyDictionary<MotionGroup, IReadOnlyDictionary<OutputIo, TeachingOutput>> teachingOutputs) : ObservableObject
{
    private CancellationTokenSource _viewCancellation = new();
    private bool _positionUpdatesActive;
    private int _manualCommandRefreshQueued;

    [ObservableProperty]
    private double _jogSpeed = 10.0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StepCommand))]
    private double _stepDistance = 0.1;

    [ObservableProperty]
    private TeachingMoveMode _moveMode;

    [ObservableProperty]
    private string? _saveError;

    public TeachingMoveMode[] MoveModes { get; } = Enum.GetValues<TeachingMoveMode>();
    public ManualControlBlock ManualBlock => state.Display.ManualBlock;
    public bool CanEditTeaching => state.Display.ManualControlsEnabled;
    public abstract TeachingMotionHint MotionHint { get; }
    public IReadOnlyList<IoStatus> IoGroups => ioGroups[CurrentMotionGroup];
    public IReadOnlyDictionary<OutputIo, TeachingOutput> TeachingOutputs => teachingOutputs[CurrentMotionGroup];
    public bool HasY => CurrentFeedback.HasY;
    public bool HasZ => CurrentFeedback.HasZ;
    public MotionStatus Motion => machine.GetMotionStatus(CurrentMotionGroup);
    protected IMotionFeedback CurrentFeedback => Motion.Feedback;

    protected abstract MotionGroup CurrentMotionGroup { get; }
    protected bool PositionUpdatesActive => _positionUpdatesActive;
    protected CancellationToken ViewCancellation => _viewCancellation.Token;
    protected abstract IReadOnlyList<TeachingPoint> CurrentPoints { get; }
    protected abstract TeachingPoint? CurrentPoint { get; set; }

    [RelayCommand(CanExecute = nameof(CanTeachCurrentPosition))]
    private Task TeachCurrentPositionAsync(CancellationToken cancellationToken) =>
        RunMotionAsync(async token =>
        {
            var point = CurrentPoint!;
            var current = CurrentFeedback.GetPosition();
            point.Teach(current.X, current.Y, current.Z);
            if (point.Storage == TeachingStorage.Buffer) return;

            point.Apply();
            RefreshPointPositions();
            if (point.Storage == TeachingStorage.Machine
                && !await SaveSettingsAsync(token, point.Position.Setting!)) return;

            token.ThrowIfCancellationRequested();
            OnPointTaught(point);
            NotifyManualTeachingCommands();
        }, cancellationToken);

    private bool CanTeachCurrentPosition() =>
        CurrentPoint is { TeachMode: not TeachMode.Image, Position.CanTeach: true }
        && CanUseCurrentHandler();

    protected abstract void RefreshPointPositions();
    protected virtual void OnPointTaught(TeachingPoint point) { }

    [RelayCommand(CanExecute = nameof(CanMoveToPoint))]
    private Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = CurrentPoint!;
        return RunMotionAsync(token => MovePointAsync(point, token), cancellationToken);
    }

    protected abstract bool CanMoveToPoint();
    protected abstract Task MovePointAsync(TeachingPoint point, CancellationToken cancellationToken);

    protected async Task<bool> SaveSettingsAsync(CancellationToken cancellationToken, params Setting[] settings)
    {
        SaveError = null;
        try
        {
            await Task.Run(() => store.SaveSettings(settings, cancellationToken), cancellationToken);
            System.Diagnostics.Trace.TraceInformation("Teaching settings saved: {0}.", CurrentMotionGroup);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Teaching settings save failed: {0}. {1}", CurrentMotionGroup, exception);
            SaveError = $"Teaching values were not saved: {exception.GetBaseException().Message}";
            return false;
        }
    }

    private int CurrentPointIndex
    {
        get
        {
            for (var index = 0; index < CurrentPoints.Count; index++)
                if (CurrentPoints[index] == CurrentPoint) return index;
            return -1;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectPreviousPoint))]
    private void SelectPreviousPoint() => CurrentPoint = CurrentPoints[CurrentPointIndex - 1];

    [RelayCommand(CanExecute = nameof(CanSelectNextPoint))]
    private void SelectNextPoint() => CurrentPoint = CurrentPoints[CurrentPointIndex + 1];

    private bool CanSelectPreviousPoint() => CurrentPointIndex > 0;
    private bool CanSelectNextPoint() => CurrentPointIndex < CurrentPoints.Count - 1;

    protected void NotifyPointSelectionCommands()
    {
        SelectPreviousPointCommand.NotifyCanExecuteChanged();
        SelectNextPointCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSetOutput), IncludeCancelCommand = true)]
    private Task SetOutputOnAsync(TeachingOutput output, CancellationToken cancellationToken) =>
        SetOutputAsync(output, true, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanSetOutput))]
    private Task SetOutputOffAsync(TeachingOutput output, CancellationToken cancellationToken) =>
        SetOutputAsync(output, false, cancellationToken);

    private bool CanSetOutput(TeachingOutput? output) =>
        output is not null
        && TeachingOutputs.ContainsKey(output.Signal)
        && (output.RequiresHandler ? CanUseCurrentHandler() : state.Display.ManualOutputsEnabled)
        && (output.CanSet?.Invoke(false) ?? true);

    protected bool CanUseCurrentHandler() => machine.CanUseManualMotion(CurrentMotionGroup, live: false);

    private async Task SetOutputAsync(TeachingOutput output, bool value, CancellationToken cancellationToken)
    {
        try
        {
            await machine.RunManualOutputAsync(output, value, cancellationToken, ViewCancellation);
        }
        finally
        {
            NotifyManualTeachingCommands();
        }
    }


    [RelayCommand(CanExecute = nameof(CanMoveDirection))]
    private Task JogAsync(TeachingDirection direction, CancellationToken cancellationToken)
    {
        var (axis, sign) = Resolve(direction);
        return RunMotionAsync(
            token => JogCurrentAsync(axis, sign * JogSpeed, token),
            cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepAsync(TeachingDirection direction, CancellationToken cancellationToken)
    {
        return RunMotionAsync(
            token =>
            {
                var (axis, target) = StepTarget(direction, CurrentFeedback.GetPosition());
                return MoveCurrentAxisAsync(axis, target, token);
            },
            cancellationToken);
    }

    private (MotionAxis Axis, double Position) StepTarget(
        TeachingDirection direction, (double X, double Y, double Z) current)
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
        if (!CanMoveDirection(direction)) return false;
        var position = Motion.Position;
        var (axis, target) = StepTarget(direction, (position.X, position.Y, position.Z));
        return CurrentFeedback.GetRange(axis) is not { } range
               || target >= range.Minimum && target <= range.Maximum;
    }

    private static (MotionAxis Axis, int Sign) Resolve(TeachingDirection direction) => direction switch
    {
        TeachingDirection.XMinus => (MotionAxis.X, -1),
        TeachingDirection.XPlus => (MotionAxis.X, 1),
        TeachingDirection.YMinus => (MotionAxis.Y, -1),
        TeachingDirection.YPlus => (MotionAxis.Y, 1),
        TeachingDirection.ZMinus => (MotionAxis.Z, -1),
        TeachingDirection.ZPlus => (MotionAxis.Z, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    private bool CanMoveDirection(TeachingDirection direction) => CanJog(Resolve(direction).Axis);

    [RelayCommand]
    private void JogStop() => CancelTeaching();

    private bool CanJogZ() => CanJog(MotionAxis.Z);
    protected abstract bool CanJog(MotionAxis axis);
    protected abstract void NotifyManualTeachingCommands();

    [RelayCommand(CanExecute = nameof(CanJogZ))]
    private Task MoveToHorizontalZAsync(CancellationToken cancellationToken) =>
        RunMotionAsync(
            MoveCurrentToHorizontalZAsync,
            cancellationToken);

    protected abstract Task JogCurrentAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken);

    protected abstract Task MoveCurrentToHorizontalZAsync(
        CancellationToken cancellationToken);

    protected abstract Task MoveCurrentAxisAsync(
        MotionAxis axis, double position, CancellationToken cancellationToken);

    protected Task RunMotionAsync(
        Func<CancellationToken, Task> move,
        CancellationToken cancellationToken) =>
        machine.RunManualMotionAsync(CurrentMotionGroup, move, cancellationToken, ViewCancellation);

    protected Task RunTeachingEditAsync(
        Func<CancellationToken, Task> edit,
        CancellationToken cancellationToken) =>
        machine.RunTeachingEditAsync(edit, cancellationToken, ViewCancellation);

    protected void CancelTeaching()
    {
        var cancellation = _viewCancellation;
        _viewCancellation = new CancellationTokenSource();
        cancellation.Cancel();
        cancellation.Dispose();
    }

    protected void NotifyMotionCommands()
    {
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
        _positionUpdatesActive = true;
        OnPropertyChanged(nameof(Motion));
    }

    public virtual void Deactivate()
    {
        _positionUpdatesActive = false;
        CancelTeaching();
    }

    protected void QueueManualCommandRefresh()
    {
        if (!PositionUpdatesActive || Interlocked.Exchange(ref _manualCommandRefreshQueued, 1) != 0)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _manualCommandRefreshQueued, 0);
            if (PositionUpdatesActive)
            {
                NotifyManualTeachingCommands();
            }
        });
    }

}
