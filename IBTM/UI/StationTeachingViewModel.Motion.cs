using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class StationTeachingViewModel
{
    private IXyMotion CurrentMotion => GetMotion(SelectedMotionGroup);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private void JogXPlus() =>
        CurrentMotion.JogX(JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private void JogXMinus() =>
        CurrentMotion.JogX(-JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private void JogYPlus() =>
        CurrentMotion.JogY(JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private void JogYMinus() =>
        CurrentMotion.JogY(-JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogZ))]
    private void JogZPlus() =>
        CurrentMotion.JogZ(JogSpeed, _motionCancellation.Token);

    [RelayCommand(CanExecute = nameof(CanJogZ))]
    private void JogZMinus() =>
        CurrentMotion.JogZ(-JogSpeed, _motionCancellation.Token);

    [RelayCommand]
    private void JogStop() => CancelMotion();

    private bool CanJogXY() =>
        CanUseCurrentHandler() && CurrentMotion.IsAtHorizontalZ;
    private bool CanJogZ() =>
        CanUseCurrentHandler() && CurrentMotion.HasZ;

    [RelayCommand(CanExecute = nameof(CanJogZ))]
    private async Task MoveToHorizontalZAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var motionCancellation = LinkMotion(cancellationToken);
            await CurrentMotion.MoveToHorizontalZAsync(motionCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveToPoint))]
    private async Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint!;
        var motion = GetMotion(point.MotionGroup);
        try
        {
            using var motionCancellation = LinkMotion(cancellationToken);
            cancellationToken = motionCancellation.Token;
            switch (point.TeachMode)
            {
                case TeachMode.XYOnly:
                    await motion.MoveToXYAsync(
                        point.X,
                        point.Y,
                        GetMotionSettings(point.MotionGroup)
                            .HorizontalSpeed,
                        cancellationToken);
                    break;
                case TeachMode.ZOnly
                    when point.Target != TeachingTarget.BoltWorkZ:
                    await motion.MoveZAsync(
                        point.Z!.Value,
                        GetMotionSettings(point.MotionGroup).ZSpeed,
                        cancellationToken);
                    break;
                default:
                    await motion.MoveToAsync(
                        point.X,
                        point.Y,
                        point.Z!.Value,
                        cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool CanMoveToPoint() =>
        CanTeachCurrentPosition()
        && _pointMapper.HasMotionPosition(
            CurrentRecipe,
            SelectedPoint!);

    private bool CanUseCurrentHandler() =>
        _state.ManualControlsEnabled
        && (SelectedMotionGroup != MotionGroup.PcbPlacementHandler
            || !_buffer.SupplyInside);

    private void NotifyManualTeachingCommands()
    {
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        JogXPlusCommand.NotifyCanExecuteChanged();
        JogXMinusCommand.NotifyCanExecuteChanged();
        JogYPlusCommand.NotifyCanExecuteChanged();
        JogYMinusCommand.NotifyCanExecuteChanged();
        JogZPlusCommand.NotifyCanExecuteChanged();
        JogZMinusCommand.NotifyCanExecuteChanged();
        MoveToHorizontalZCommand.NotifyCanExecuteChanged();
        MoveToPointCommand.NotifyCanExecuteChanged();
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        CaptureCarrierImagesCommand.NotifyCanExecuteChanged();
        TeachImagePointCommand.NotifyCanExecuteChanged();
    }

    private MotionSettings GetMotionSettings(MotionGroup group) => group switch
    {
        MotionGroup.PcbPlacementHandler => _placementSettings.Motion,
        MotionGroup.BoltFastening => _fasteningSettings.Motion,
        MotionGroup.InspectionGantry => _inspectionGantrySettings.Motion,
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    private IXyMotion GetMotion(MotionGroup motionGroup) => motionGroup switch
    {
        MotionGroup.PcbPlacementHandler => _pcbPlacementMotion,
        MotionGroup.BoltFastening => _boltFasteningMotion,
        MotionGroup.InspectionGantry => _inspectionGantryMotion,
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

    private void RefreshPosition()
    {
        OnPropertyChanged(nameof(CurrentX));
        OnPropertyChanged(nameof(CurrentY));
        OnPropertyChanged(nameof(CurrentZ));
        OnPropertyChanged(nameof(CameraFieldOfView));
        JogXPlusCommand.NotifyCanExecuteChanged();
        JogXMinusCommand.NotifyCanExecuteChanged();
        JogYPlusCommand.NotifyCanExecuteChanged();
        JogYMinusCommand.NotifyCanExecuteChanged();
    }

    private void ApplyPosition(MotionGroup motionGroup)
    {
        if (motionGroup != SelectedMotionGroup
            || Interlocked.Exchange(ref _positionRefreshQueued, 1) != 0)
        {
            return;
        }

        RunOnUi(() =>
        {
            Interlocked.Exchange(ref _positionRefreshQueued, 0);
            RefreshPosition();
        });
    }
}
