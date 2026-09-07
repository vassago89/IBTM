using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

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
    OperationCancellation operations,
    MachineState state,
    IReadOnlyDictionary<MotionGroup, IoStatus[]> ioGroups,
    IReadOnlyDictionary<MotionGroup, IReadOnlyDictionary<OutputIo, TeachingOutput>> teachingOutputs) : ObservableObject
{
    private CancellationTokenSource _motionCancellation = new();
    private MotionPosition _position = new(0, 0, 0);
    private bool _positionUpdatesActive;
    private int _positionRefreshQueued;

    [ObservableProperty]
    private double _jogSpeed = 10.0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StepCommand))]
    private double _stepDistance = 0.1;

    [ObservableProperty]
    private TeachingMoveMode _moveMode;

    public TeachingMoveMode[] MoveModes { get; } = Enum.GetValues<TeachingMoveMode>();
    public ManualControlBlock ManualBlock => state.ManualBlock;
    public bool CanEditTeaching => state.ManualControlsEnabled;
    public abstract TeachingMotionHint MotionHint { get; }
    public IReadOnlyList<IoStatus> IoGroups => ioGroups[CurrentMotionGroup];
    public IReadOnlyDictionary<OutputIo, TeachingOutput> TeachingOutputs => teachingOutputs[CurrentMotionGroup];
    public bool HasY => CurrentFeedback.HasY;
    public bool HasZ => CurrentFeedback.HasZ;
    protected abstract IMotionFeedback CurrentFeedback { get; }

    protected abstract MotionGroup CurrentMotionGroup { get; }
    protected bool PositionUpdatesActive => _positionUpdatesActive;
    protected abstract IReadOnlyList<TeachingPoint> CurrentPoints { get; }
    protected abstract TeachingPoint? CurrentPoint { get; set; }

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

    [RelayCommand(CanExecute = nameof(CanSetOutput))]
    private Task SetOutputOnAsync(TeachingOutput output, CancellationToken cancellationToken) =>
        SetOutputAsync(output, true, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanSetOutput))]
    private Task SetOutputOffAsync(TeachingOutput output, CancellationToken cancellationToken) =>
        SetOutputAsync(output, false, cancellationToken);

    private bool CanSetOutput(TeachingOutput? output) =>
        output is not null
        && TeachingOutputs.ContainsKey(output.Signal)
        && CanUseCurrentHandler()
        && CanUseTeachingOutputs
        && (output.CanSet?.Invoke() ?? true);

    protected abstract bool CanUseCurrentHandler();
    protected virtual bool CanUseTeachingOutputs => true;

    private async Task SetOutputAsync(TeachingOutput output, bool value, CancellationToken cancellationToken)
    {
        var group = CurrentMotionGroup;
        try
        {
            await RunMotionAsync(token => output.SetAsync(value, token), cancellationToken);
        }
        catch (IoTimeoutException exception)
        {
            state.SetError(group switch
            {
                MotionGroup.PcbSupply => MachineAlarm.PcbSupply,
                MotionGroup.PcbPlacementHandler => MachineAlarm.PcbPlacement,
                MotionGroup.BoltFastening => MachineAlarm.BoltFastening,
                MotionGroup.InspectionGantry => MachineAlarm.NgCarrierTransfer,
                _ => throw new ArgumentOutOfRangeException(nameof(group)),
            }, exception);
        }
        finally
        {
            NotifyManualTeachingCommands();
        }
    }

    public double CurrentX => _position.X;
    public double CurrentY => _position.Y;
    public double CurrentZ => _position.Z;

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
        var (axis, target) = StepTarget(direction);
        return RunMotionAsync(
            token => MoveCurrentAxisAsync(axis, target, token),
            cancellationToken);
    }

    private (MotionAxis Axis, double Position) StepTarget(TeachingDirection direction)
    {
        var (axis, sign) = Resolve(direction);
        var current = CurrentPosition();
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
        var (axis, target) = StepTarget(direction);
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
    private void JogStop() => CancelMotion();

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

    protected (double X, double Y, double Z) CurrentPosition() => CurrentFeedback.GetPosition();

    protected async Task RunMotionAsync(
        Func<CancellationToken, Task> move,
        CancellationToken cancellationToken)
    {
        try
        {
            using var motionCancellation = LinkMotion(cancellationToken);
            await move(motionCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (MotionException exception)
        {
            state.SetError(MachineAlarm.MotionUnavailable, exception);
            operations.Cancel();
        }
    }

    protected OperationCancellation.Operation LinkMotion(
        CancellationToken cancellationToken) =>
        operations.Link(
            cancellationToken,
            _motionCancellation.Token);

    protected void CancelMotion()
    {
        var cancellation = _motionCancellation;
        _motionCancellation = new CancellationTokenSource();
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
        OnPropertyChanged(nameof(ManualBlock));
        OnPropertyChanged(nameof(CanEditTeaching));
        OnPropertyChanged(nameof(MotionHint));
    }

    protected virtual void RefreshPosition()
    {
        var position = CurrentPosition();
        _position = new(position.X, position.Y, position.Z);
        RefreshPositionBindings();
    }

    protected virtual void RefreshPositionBindings()
    {
        OnPropertyChanged(nameof(CurrentX));
        OnPropertyChanged(nameof(CurrentY));
        OnPropertyChanged(nameof(CurrentZ));
    }

    protected void ActivatePositionUpdates()
    {
        _positionUpdatesActive = true;
        RefreshPosition();
    }

    protected void DeactivatePositionUpdates() =>
        _positionUpdatesActive = false;

    protected void QueuePositionRefresh(
        MotionGroup motionGroup,
        double x,
        double y,
        double z)
    {
        if (!_positionUpdatesActive
            || motionGroup != CurrentMotionGroup)
        {
            return;
        }

        _position = new(x, y, z);
        if (Interlocked.Exchange(ref _positionRefreshQueued, 1) != 0)
        {
            return;
        }

        Application.Current.Dispatcher.InvokeAsync(
            () =>
            {
                Interlocked.Exchange(ref _positionRefreshQueued, 0);
                if (_positionUpdatesActive)
                {
                    RefreshPositionBindings();
                }
            },
            DispatcherPriority.Background);
    }

    protected void QueueManualCommandRefresh()
    {
        if (!PositionUpdatesActive)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (PositionUpdatesActive)
            {
                NotifyManualTeachingCommands();
            }
        });
    }

    protected void QueueManualCommandRefresh(bool _) =>
        QueueManualCommandRefresh();
}
