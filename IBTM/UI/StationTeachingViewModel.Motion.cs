using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class StationTeachingViewModel
{
    public override TeachingMotionHint MotionHint =>
        SelectedMotionGroup == MotionGroup.PcbPlacementHandler && _buffer.SupplyInside
            ? TeachingMotionHint.SupplyInBuffer
            : SelectedMotionGroup == MotionGroup.PcbPlacementHandler
              && !_placementHandler.CanMoveHorizontal
                ? TeachingMotionHint.RaisePlacementCylinders
                : SelectedMotionGroup == MotionGroup.BoltFastening
                  && !_fasteningGantry.CanMoveHorizontal
                    ? TeachingMotionHint.RaiseFasteningCylinders
            : SelectedMotionGroup == MotionGroup.InspectionGantry && !_inspectionGantry.CanMove
                ? TeachingMotionHint.RaiseNgPickup
                : !CurrentFeedback.IsAtHorizontalZ
                    ? TeachingMotionHint.TravelZRequired
                    : TeachingMotionHint.None;

    protected override MotionGroup CurrentMotionGroup =>
        SelectedMotionGroup;
    protected override IMotionFeedback CurrentFeedback => SelectedMotionGroup switch
    {
        MotionGroup.PcbPlacementHandler => _placementHandler.Feedback,
        MotionGroup.BoltFastening => _fasteningGantry.Feedback,
        MotionGroup.InspectionGantry => _inspectionGantry.Feedback,
        _ => throw new ArgumentOutOfRangeException(nameof(SelectedMotionGroup)),
    };

    protected override bool CanJog(MotionAxis axis) =>
        CanUseCurrentHandler()
        && (axis == MotionAxis.Z
            ? CurrentFeedback.HasZ
            : CanMoveHorizontal()
              && CurrentFeedback.IsAtHorizontalZ);

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

    protected override Task MoveCurrentAxisAsync(
        MotionAxis axis, double position, CancellationToken cancellationToken)
    {
        var current = CurrentPosition();
        var x = axis == MotionAxis.X ? position : current.X;
        var y = axis == MotionAxis.Y ? position : current.Y;
        return (SelectedMotionGroup, axis) switch
        {
            (MotionGroup.PcbPlacementHandler, MotionAxis.X) => _placementHandler.MoveXAsync(position, cancellationToken),
            (MotionGroup.PcbPlacementHandler, MotionAxis.Y) => _placementHandler.MoveYAsync(position, cancellationToken),
            (MotionGroup.PcbPlacementHandler, MotionAxis.Z) => _placementHandler.MoveZAsync(position, cancellationToken),
            (MotionGroup.BoltFastening, MotionAxis.Z) => _fasteningGantry.MoveZAsync(position, cancellationToken),
            (MotionGroup.BoltFastening, _) => _fasteningGantry.MoveToXYAsync(x, y, cancellationToken),
            (MotionGroup.InspectionGantry, _) => _inspectionGantry.MoveToAsync(
                new AxisPosition { X = x, Y = y }, _inspectionGantrySettings.Motion.HorizontalSpeed, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
    }

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
        SelectedPoint is not null
        && CanUseCurrentHandler()
        && (SelectedPoint.TeachMode == TeachMode.ZOnly
            || CanMoveHorizontal())
        && _teachingPoints.HasMotionPosition(
            CurrentRecipe,
            SelectedPoint!);

    private bool CanMoveHorizontal() => SelectedMotionGroup switch
    {
        MotionGroup.PcbPlacementHandler => _placementHandler.CanMoveHorizontal,
        MotionGroup.BoltFastening => _fasteningGantry.CanMoveHorizontal,
        MotionGroup.InspectionGantry => _inspectionGantry.CanMove,
        _ => false,
    };

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
