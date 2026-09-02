using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class StationTeachingViewModel
{
    protected override MotionGroup CurrentMotionGroup =>
        SelectedMotionGroup;
    private IMotionFeedback CurrentFeedback =>
        Feedback(SelectedMotionGroup);

    protected override bool CanJog(MotionAxis axis) =>
        CanUseCurrentHandler()
        && (axis == MotionAxis.Z
            ? CurrentFeedback.HasZ
            : CurrentFeedback.IsAtHorizontalZ);

    protected override (double X, double Y, double Z) CurrentPosition() =>
        CurrentFeedback.GetPosition();

    protected override void JogCurrent(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken)
    {
        switch (SelectedMotionGroup)
        {
            case MotionGroup.PcbPlacementHandler:
                _placementHandler.Jog(axis, velocity, cancellationToken);
                break;
            case MotionGroup.BoltFastening:
                _fasteningStation.Jog(axis, velocity, cancellationToken);
                break;
            case MotionGroup.InspectionGantry:
                _inspectionGantry.Jog(axis, velocity, cancellationToken);
                break;
        }
    }

    protected override Task MoveCurrentToHorizontalZAsync(
        CancellationToken cancellationToken) =>
        SelectedMotionGroup switch
        {
            MotionGroup.PcbPlacementHandler =>
                _placementHandler.MoveToHorizontalZAsync(cancellationToken),
            MotionGroup.BoltFastening =>
                _fasteningStation.MoveToSafeZAsync(cancellationToken),
            MotionGroup.InspectionGantry => Task.CompletedTask,
            _ => throw new ArgumentOutOfRangeException(),
        };

    [RelayCommand(CanExecute = nameof(CanMoveToPoint))]
    private async Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint!;
        await RunMotionAsync(
            moveCancellation => MovePointAsync(point, moveCancellation),
            cancellationToken);
    }

    private Task MovePointAsync(
        TeachingPoint point,
        CancellationToken cancellationToken) =>
        point.MotionGroup switch
        {
            MotionGroup.PcbPlacementHandler =>
                MovePlacementPointAsync(point, cancellationToken),
            MotionGroup.BoltFastening =>
                MoveFasteningPointAsync(point, cancellationToken),
            MotionGroup.InspectionGantry =>
                _inspectionGantry.MoveToAsync(
                    new AxisPosition { X = point.X, Y = point.Y },
                    _inspectionGantrySettings.Motion.HorizontalSpeed,
                    cancellationToken),
            _ => throw new ArgumentOutOfRangeException(),
        };

    private Task MovePlacementPointAsync(
        TeachingPoint point,
        CancellationToken cancellationToken) =>
        point.TeachMode switch
        {
            TeachMode.XYOnly => _placementHandler.MoveToXYAsync(
                point.X,
                point.Y,
                cancellationToken),
            TeachMode.ZOnly
                when point.Target != TeachingTarget.BoltPointZ =>
                _placementHandler.MoveZAsync(
                    point.Z!.Value,
                    cancellationToken),
            _ => _placementHandler.MoveToAsync(
                point.X,
                point.Y,
                point.Z!.Value,
                cancellationToken),
        };

    private Task MoveFasteningPointAsync(
        TeachingPoint point,
        CancellationToken cancellationToken) =>
        point.TeachMode switch
        {
            TeachMode.XYOnly => _fasteningStation.MoveToXYAsync(
                point.X,
                point.Y,
                cancellationToken),
            TeachMode.ZOnly
                when point.Target != TeachingTarget.BoltPointZ =>
                _fasteningStation.MoveZAsync(
                    point.Z!.Value,
                    cancellationToken),
            _ => _fasteningStation.MoveToAsync(
                point.X,
                point.Y,
                point.Z!.Value,
                cancellationToken),
        };

    private bool CanMoveToPoint() =>
        CanTeachCurrentPosition()
        && _pointMapper.HasMotionPosition(
            CurrentRecipe,
            SelectedPoint!);

    private bool CanUseCurrentHandler() =>
        _state.ManualControlsEnabled
        && (SelectedMotionGroup != MotionGroup.PcbPlacementHandler
            || !_buffer.SupplyInside);

    protected override void NotifyManualTeachingCommands()
    {
        NotifyMotionCommands();
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        MoveToPointCommand.NotifyCanExecuteChanged();
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        CaptureCarrierImagesCommand.NotifyCanExecuteChanged();
        TeachImagePointCommand.NotifyCanExecuteChanged();
    }

    private IMotionFeedback Feedback(MotionGroup group) => group switch
    {
        MotionGroup.PcbPlacementHandler => _placementHandler.Feedback,
        MotionGroup.BoltFastening => _fasteningStation.Feedback,
        MotionGroup.InspectionGantry => _inspectionGantry.Feedback,
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    protected override void RefreshPositionBindings()
    {
        base.RefreshPositionBindings();
        OnPropertyChanged(nameof(CameraFieldOfView));
    }
}
