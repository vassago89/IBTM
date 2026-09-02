using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public abstract partial class TeachingMotionViewModel : ObservableObject
{
    private sealed record DisplayPosition(double X, double Y, double Z);

    private CancellationTokenSource _motionCancellation = new();
    private DisplayPosition _position = new(0, 0, 0);
    private bool _positionUpdatesActive;
    private int _positionRefreshQueued;

    [ObservableProperty]
    private double _jogSpeed = 10.0;

    protected abstract MotionGroup CurrentMotionGroup { get; }
    protected bool PositionUpdatesActive => _positionUpdatesActive;

    public double CurrentX => _position.X;
    public double CurrentY => _position.Y;
    public double CurrentZ => _position.Z;

    [RelayCommand(CanExecute = nameof(CanJogX))]
    private void JogXPlus() =>
        JogCurrent(MotionAxis.X, JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogX))]
    private void JogXMinus() =>
        JogCurrent(MotionAxis.X, -JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogY))]
    private void JogYPlus() =>
        JogCurrent(MotionAxis.Y, JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogY))]
    private void JogYMinus() =>
        JogCurrent(MotionAxis.Y, -JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogZ))]
    private void JogZPlus() =>
        JogCurrent(MotionAxis.Z, JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogZ))]
    private void JogZMinus() =>
        JogCurrent(MotionAxis.Z, -JogSpeed, _motionCancellation.Token);

    [RelayCommand]
    private void JogStop() => CancelMotion();

    private bool CanJogX() => CanJog(MotionAxis.X);
    private bool CanJogY() => CanJog(MotionAxis.Y);
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

    protected abstract (double X, double Y, double Z) CurrentPosition();

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

    protected CancellationTokenSource LinkMotion(
        CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(
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
        JogXPlusCommand.NotifyCanExecuteChanged();
        JogXMinusCommand.NotifyCanExecuteChanged();
        JogYPlusCommand.NotifyCanExecuteChanged();
        JogYMinusCommand.NotifyCanExecuteChanged();
        JogZPlusCommand.NotifyCanExecuteChanged();
        JogZMinusCommand.NotifyCanExecuteChanged();
        MoveToHorizontalZCommand.NotifyCanExecuteChanged();
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
