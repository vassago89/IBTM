using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class SupplyTeachingViewModel
{
    public MotionGroup ActiveMotionGroup =>
        SelectedPoint?.MotionGroup ?? MotionGroup.PcbSupply;
    public bool HasY => CurrentMotion.HasY;
    public double SafeZ => CurrentSettings.SafeZ;

    private IAxisMotion CurrentMotion => GetMotion(ActiveMotionGroup);
    private MotionSettings CurrentSettings =>
        GetMotionSettings(ActiveMotionGroup);

    partial void OnSelectedPointChanged(
        TeachingPoint? oldValue,
        TeachingPoint? newValue)
    {
        CancelMotion();

        OnPropertyChanged(nameof(ActiveMotionGroup));
        OnPropertyChanged(nameof(HasY));
        OnPropertyChanged(nameof(SafeZ));
        NotifyManualTeachingCommands();
        RefreshPosition();
    }

    [RelayCommand(CanExecute = nameof(CanJogX))]
    private void JogXPlus() =>
        CurrentMotion.JogX(JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogX))]
    private void JogXMinus() =>
        CurrentMotion.JogX(-JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogY))]
    private void JogYPlus() =>
        CurrentMotion.JogY(JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogY))]
    private void JogYMinus() =>
        CurrentMotion.JogY(-JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanUseCurrentHandler))]
    private void JogZPlus() =>
        CurrentMotion.JogZ(JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanUseCurrentHandler))]
    private void JogZMinus() =>
        CurrentMotion.JogZ(-JogSpeed, _motionCancellation.Token);

    [RelayCommand]
    private void JogStop() => CancelMotion();

    private bool CanJogX() =>
        CanUseCurrentHandler() && CurrentMotion.IsAtSafeZ;
    private bool CanJogY() =>
        CanUseCurrentHandler() && HasY && CurrentMotion.IsAtSafeZ;

    [RelayCommand(CanExecute = nameof(CanUseCurrentHandler))]
    private async Task MoveToSafeZAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var motionCancellation = LinkMotion(cancellationToken);
            await CurrentMotion.MoveToSafeZAsync(motionCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanTeachCurrentPosition))]
    private async Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint!;
        try
        {
            using var motionCancellation = LinkMotion(cancellationToken);
            cancellationToken = motionCancellation.Token;
            if (point.MotionGroup == MotionGroup.PcbSupply)
            {
                await MoveSupplyPointAsync(point, cancellationToken);
            }
            else
            {
                await MovePlacementPointAsync(point, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task MoveSupplyPointAsync(
        TeachingPoint point,
        CancellationToken cancellationToken)
    {
        var speed = _supplySettings.Motion;
        switch (point.TeachMode)
        {
            case TeachMode.XOnly:
                await _supplyMotion.MoveXAsync(
                    point.X,
                    speed.HorizontalSpeed,
                    cancellationToken);
                break;
            case TeachMode.YOnly:
                await _supplyMotion.MoveYAsync(
                    point.Y,
                    speed.HorizontalSpeed,
                    cancellationToken);
                break;
            case TeachMode.ZOnly:
                await _supplyMotion.MoveZAsync(
                    point.Z,
                    speed.ZSpeed,
                    cancellationToken);
                break;
            case TeachMode.XYOnly:
                await MoveSupplyHorizontalAsync(
                    point.X,
                    point.Y,
                    cancellationToken);
                break;
            case TeachMode.XZOnly:
                await _supplyMotion.MoveXAsync(
                    point.X,
                    speed.HorizontalSpeed,
                    cancellationToken);
                await _supplyMotion.MoveZAsync(
                    point.Z,
                    speed.ZSpeed,
                    cancellationToken);
                break;
            case TeachMode.Full:
                await MoveSupplyHorizontalAsync(
                    point.X,
                    point.Y,
                    cancellationToken);
                await _supplyMotion.MoveZAsync(
                    point.Z,
                    speed.ZSpeed,
                    cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private async Task MoveSupplyHorizontalAsync(
        double x,
        double y,
        CancellationToken cancellationToken)
    {
        var velocity = _supplySettings.Motion.HorizontalSpeed;
        await _supplyMotion.MoveYAsync(y, velocity, cancellationToken);
        await _supplyMotion.MoveXAsync(x, velocity, cancellationToken);
    }

    private async Task MovePlacementPointAsync(
        TeachingPoint point,
        CancellationToken cancellationToken)
    {
        var speed = _placementSettings.Motion;
        switch (point.TeachMode)
        {
            case TeachMode.XOnly:
                await _placementMotion.MoveXAsync(
                    point.X,
                    speed.HorizontalSpeed,
                    cancellationToken);
                break;
            case TeachMode.YOnly:
                await _placementMotion.MoveYAsync(
                    point.Y,
                    speed.HorizontalSpeed,
                    cancellationToken);
                break;
            case TeachMode.ZOnly:
                await _placementMotion.MoveZAsync(
                    point.Z,
                    speed.ZSpeed,
                    cancellationToken);
                break;
            case TeachMode.XYOnly:
                await _placementMotion.MoveToXYAsync(
                    point.X,
                    point.Y,
                    speed.HorizontalSpeed,
                    cancellationToken);
                break;
            case TeachMode.XZOnly:
                await _placementMotion.MoveToAsync(
                    point.X,
                    _placementMotion.GetPosition().Y,
                    point.Z,
                    cancellationToken);
                break;
            case TeachMode.Full:
                await _placementMotion.MoveToAsync(
                    point.X,
                    point.Y,
                    point.Z,
                    cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private MotionSettings GetMotionSettings(MotionGroup group) => group switch
    {
        MotionGroup.PcbSupply => _supplySettings.Motion,
        MotionGroup.PcbPlacementHandler => _placementSettings.Motion,
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    private bool CanUseCurrentHandler() =>
        CanUseHandler(ActiveMotionGroup);

    private bool CanUseHandler(MotionGroup motionGroup) =>
        _state.CanOperate
        && !GetMotion(motionGroup).IsMoving
        && motionGroup switch
        {
            MotionGroup.PcbSupply =>
                !_buffer.PlacementInside,
            MotionGroup.PcbPlacementHandler =>
                !_buffer.SupplyInside,
            _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
        };

    private void NotifyManualTeachingCommands()
    {
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        JogXPlusCommand.NotifyCanExecuteChanged();
        JogXMinusCommand.NotifyCanExecuteChanged();
        JogYPlusCommand.NotifyCanExecuteChanged();
        JogYMinusCommand.NotifyCanExecuteChanged();
        JogZPlusCommand.NotifyCanExecuteChanged();
        JogZMinusCommand.NotifyCanExecuteChanged();
        MoveToSafeZCommand.NotifyCanExecuteChanged();
        MoveToPointCommand.NotifyCanExecuteChanged();
        ToggleActuatorCommand.NotifyCanExecuteChanged();
    }

    private void RefreshPosition()
    {
        OnPropertyChanged(nameof(CurrentX));
        OnPropertyChanged(nameof(CurrentY));
        OnPropertyChanged(nameof(CurrentZ));
        JogXPlusCommand.NotifyCanExecuteChanged();
        JogXMinusCommand.NotifyCanExecuteChanged();
        JogYPlusCommand.NotifyCanExecuteChanged();
        JogYMinusCommand.NotifyCanExecuteChanged();
    }

    private IAxisMotion GetMotion(MotionGroup motionGroup) => motionGroup switch
    {
        MotionGroup.PcbSupply => _supplyMotion,
        MotionGroup.PcbPlacementHandler => _placementMotion,
        _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
    };

    private CancellationTokenSource LinkMotion(
        CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _motionCancellation.Token);

    private void CancelMotion()
    {
        var cancellation = _motionCancellation;
        _motionCancellation = new CancellationTokenSource();
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void ApplyPosition(MotionGroup motionGroup)
    {
        if (motionGroup != ActiveMotionGroup)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(RefreshPosition);
    }
}
