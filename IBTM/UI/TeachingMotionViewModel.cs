using System;
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
    [Description("Raise the placement handler and IPM before moving X/Y.")] RaisePlacementCylinders,
    [Description("Raise the fastening table and both heads before moving X/Y.")] RaiseFasteningCylinders,
    [Description("Move to Travel Z before moving X/Y.")] TravelZRequired,
    [Description("Inside buffer: Y and Z moves are disabled.")] SupplyInBufferRestricted,
}

public abstract partial class TeachingMotionViewModel(
    OperationCancellation operations,
    MachineState state) : ObservableObject
{
    private sealed record DisplayPosition(double X, double Y, double Z);

    private CancellationTokenSource _motionCancellation = new();
    private DisplayPosition _position = new(0, 0, 0);
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
    public abstract TeachingMotionHint MotionHint { get; }
    public bool HasY => CurrentFeedback.HasY;
    public bool HasZ => CurrentFeedback.HasZ;
    protected abstract IMotionFeedback CurrentFeedback { get; }

    protected abstract MotionGroup CurrentMotionGroup { get; }
    protected bool PositionUpdatesActive => _positionUpdatesActive;

    public double CurrentX => _position.X;
    public double CurrentY => _position.Y;
    public double CurrentZ => _position.Z;

    [RelayCommand(CanExecute = nameof(CanMoveDirection))]
    private void Jog(TeachingDirection direction)
    {
        var (axis, sign) = Resolve(direction);
        JogCurrent(axis, sign * JogSpeed, _motionCancellation.Token);
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

    protected abstract void JogCurrent(
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
        OnPropertyChanged(nameof(ManualBlock));
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
