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

    private MotionService CurrentMotion => GetMotion(ActiveMotionGroup);
    private MotionSettings CurrentSettings => GetSettings(ActiveMotionGroup);

    partial void OnSelectedPointChanged(
        TeachingPoint? oldValue,
        TeachingPoint? newValue)
    {
        if (oldValue is not null)
        {
            GetMotion(oldValue.MotionGroup).Stop();
        }

        OnPropertyChanged(nameof(ActiveMotionGroup));
        OnPropertyChanged(nameof(HasY));
        OnPropertyChanged(nameof(SafeZ));
        JogXPlusCommand.NotifyCanExecuteChanged();
        JogXMinusCommand.NotifyCanExecuteChanged();
        JogYPlusCommand.NotifyCanExecuteChanged();
        JogYMinusCommand.NotifyCanExecuteChanged();
        RefreshPosition();
    }

    [RelayCommand(CanExecute = nameof(CanJogX))]
    private void JogXPlus() => CurrentMotion.JogX(JogSpeed);

    [RelayCommand(CanExecute = nameof(CanJogX))]
    private void JogXMinus() => CurrentMotion.JogX(-JogSpeed);

    [RelayCommand(CanExecute = nameof(CanJogY))]
    private void JogYPlus() => CurrentMotion.JogY(JogSpeed);

    [RelayCommand(CanExecute = nameof(CanJogY))]
    private void JogYMinus() => CurrentMotion.JogY(-JogSpeed);

    [RelayCommand]
    private void JogZPlus() => CurrentMotion.JogZ(JogSpeed);

    [RelayCommand]
    private void JogZMinus() => CurrentMotion.JogZ(-JogSpeed);

    [RelayCommand]
    private void JogStop() => CurrentMotion.Stop();

    private bool CanJogX() => CurrentMotion.IsAtSafeZ;
    private bool CanJogY() => HasY && CurrentMotion.IsAtSafeZ;

    [RelayCommand]
    private async Task MoveToSafeZAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CurrentMotion.MoveToSafeZAsync(cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Move stopped";
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveToPoint))]
    private async Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint!;
        var motion = GetMotion(point.MotionGroup);
        var settings = GetSettings(point.MotionGroup);

        try
        {
            switch (point.TeachMode)
            {
                case TeachMode.XOnly:
                    await motion.MoveToXAsync(
                        point.X,
                        settings.HorizontalSpeed,
                        cancellationToken);
                    break;
                case TeachMode.XZOnly:
                    await motion.MoveToXZAsync(
                        point.X,
                        point.Z,
                        cancellationToken);
                    break;
                case TeachMode.Full:
                    await motion.MoveToAsync(
                        point.X,
                        point.Y,
                        point.Z,
                        cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unsupported handler teach mode: "
                        + $"{point.TeachMode.GetDescription()}.");
            }

            StatusMessage = $"Moved to: {point.Name}";
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Move stopped";
        }
    }

    private bool CanMoveToPoint() => SelectedPoint?.IsTaught == true;

    private void RefreshPosition()
    {
        var position = CurrentMotion.GetPosition();
        CurrentX = position.X;
        CurrentY = position.Y;
        CurrentZ = position.Z;
    }

    private MotionService GetMotion(MotionGroup motionGroup) => motionGroup switch
    {
        MotionGroup.PcbSupply => _supplyMotion,
        MotionGroup.PcbPlacement => _placementMotion,
        _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
    };

    private MotionSettings GetSettings(MotionGroup motionGroup) => motionGroup switch
    {
        MotionGroup.PcbSupply => _settings.PcbSupply.Motion,
        MotionGroup.PcbPlacement => _settings.PcbPlacement.Motion,
        _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
    };

    private void ApplyPosition(
        MotionGroup motionGroup,
        double x,
        double y,
        double z)
    {
        if (motionGroup != ActiveMotionGroup)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            CurrentX = x;
            CurrentY = y;
            CurrentZ = z;
        });
    }
}
