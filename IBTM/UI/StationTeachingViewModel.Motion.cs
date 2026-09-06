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
    private IMotionFeedback CurrentFeedback => SelectedMotionGroup switch
    {
        MotionGroup.PcbPlacementHandler => _placementHandler.Feedback,
        MotionGroup.BoltFastening => _fasteningGantry.Feedback,
        MotionGroup.InspectionGantry => _inspectionGantry.Feedback,
        _ => throw new ArgumentOutOfRangeException(nameof(SelectedMotionGroup)),
    };

    protected override bool CanJog(MotionAxis axis) =>
        CanUseCurrentHandler()
        && (SelectedMotionGroup != MotionGroup.InspectionGantry
            || _inspectionGantry.CanMove)
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
                _fasteningGantry.Jog(axis, velocity, cancellationToken);
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
                _fasteningGantry.MoveToSafeZAsync(cancellationToken),
            MotionGroup.InspectionGantry => Task.CompletedTask,
            _ => throw new ArgumentOutOfRangeException(),
        };

    [RelayCommand(CanExecute = nameof(CanMoveToPoint))]
    private Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint!;
        return RunMotionAsync(
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
            TeachMode.ZOnly =>
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
            TeachMode.XYOnly => _fasteningGantry.MoveToXYAsync(
                point.X,
                point.Y,
                cancellationToken),
            TeachMode.ZOnly
                when point.Target != TeachingTarget.BoltPointZ =>
                _fasteningGantry.MoveZAsync(
                    point.Z!.Value,
                    cancellationToken),
            _ => _fasteningGantry.MoveToAsync(
                point.X,
                point.Y,
                point.Z!.Value,
                cancellationToken),
        };

    private bool CanMoveToPoint() =>
        CanTeachCurrentPosition()
        && (SelectedMotionGroup != MotionGroup.InspectionGantry
            || _inspectionGantry.CanMove)
        && _teachingPoints.HasMotionPosition(
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

    protected override void RefreshPositionBindings()
    {
        base.RefreshPositionBindings();
        OnPropertyChanged(nameof(CameraFieldOfView));
    }
}
