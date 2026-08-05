using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class StationTeachingViewModel
{
    private MotionService CurrentMotion => GetMotion(SelectedMotionGroup);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private void JogXPlus() => CurrentMotion.JogX(JogSpeed);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private void JogXMinus() => CurrentMotion.JogX(-JogSpeed);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private void JogYPlus() => CurrentMotion.JogY(JogSpeed);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private void JogYMinus() => CurrentMotion.JogY(-JogSpeed);

    [RelayCommand]
    private void JogZPlus() => CurrentMotion.JogZ(JogSpeed);

    [RelayCommand]
    private void JogZMinus() => CurrentMotion.JogZ(-JogSpeed);

    [RelayCommand]
    private void JogStop() => CurrentMotion.Stop();

    private bool CanJogXY() => CurrentMotion.IsAtSafeZ;

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
        try
        {
            switch (point.TeachMode)
            {
                case TeachMode.XOnly:
                    await motion.MoveToXAsync(
                        point.X,
                        GetMotionSettings(point.MotionGroup).HorizontalSpeed,
                        cancellationToken);
                    break;
                case TeachMode.XZOnly:
                    await motion.MoveToXZAsync(
                        point.X,
                        point.Z,
                        cancellationToken);
                    break;
                case TeachMode.XYOnly:
                    await motion.MoveToXYAsync(
                        point.X,
                        point.Y,
                        GetMotionSettings(point.MotionGroup).HorizontalSpeed,
                        cancellationToken);
                    break;
                default:
                    await motion.MoveToAsync(
                        point.X,
                        point.Y,
                        point.Z,
                        cancellationToken);
                    break;
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

    private MotionService GetMotion(MotionGroup motionGroup) =>
        _motions[motionGroup];

    private MotionSettings GetMotionSettings(MotionGroup motionGroup) =>
        motionGroup switch
        {
            MotionGroup.PcbPlacement => _settings.PcbPlacement.Motion,
            MotionGroup.BoltFastening => _settings.BoltFastening.Motion,
            MotionGroup.Inspection => _settings.Inspection.Motion,
            _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
        };

    private void RefreshPosition()
    {
        var current = CurrentMotion.GetPosition();
        CurrentX = current.X;
        CurrentY = current.Y;
        CurrentZ = current.Z;
    }

    private void ApplyPosition(
        MotionGroup motionGroup,
        double x,
        double y,
        double z) =>
        RunOnUi(() =>
        {
            if (SelectedMotionGroup == motionGroup)
            {
                CurrentX = x;
                CurrentY = y;
                CurrentZ = z;
            }
        });
}
