using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class StationTeachingViewModel
{
    public override TeachingMotionHint MotionHint => SelectedMotionGroup switch
    {
        MotionGroup.PcbPlacementHandler when _buffer.SupplyInside =>
            TeachingMotionHint.SupplyInBuffer,
        MotionGroup.PcbPlacementHandler when !_placementHandler.CanMoveHorizontal =>
            TeachingMotionHint.RaisePlacementCylinders,
        MotionGroup.BoltFastening => TeachingMotionHint.BoltAdjustment,
        MotionGroup.InspectionGantry when !_inspectionGantry.CanMove =>
            TeachingMotionHint.RaiseNgPickup,
        _ when !CurrentFeedback.IsAtHorizontalZ => TeachingMotionHint.SafeZRequired,
        _ => TeachingMotionHint.None,
    };

    protected override MotionGroup CurrentMotionGroup =>
        SelectedMotionGroup;

    protected override bool CanJog(MotionAxis axis) =>
        CanUseCurrentHandler()
        && (axis == MotionAxis.Z
            ? CurrentFeedback.HasZ
            : SelectedMotionGroup == MotionGroup.BoltFastening
              || CanMoveHorizontal() && CurrentFeedback.IsAtHorizontalZ);

    protected override Task JogCurrentAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken) =>
        SelectedMotionGroup switch
        {
            MotionGroup.PcbPlacementHandler => _placementHandler.JogAsync(axis, velocity, cancellationToken),
            MotionGroup.BoltFastening => _fasteningGantry.JogAsync(axis, velocity, cancellationToken),
            MotionGroup.InspectionGantry => _inspectionGantry.JogAsync(axis, velocity, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(SelectedMotionGroup)),
        };

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
            (MotionGroup.BoltFastening, _) => _fasteningGantry.AdjustAxisAsync(axis, position, JogSpeed, cancellationToken),
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

    [RelayCommand(CanExecute = nameof(CanReturnFromPickup))]
    private Task ReturnFromPickupAsync(CancellationToken cancellationToken) =>
        RunMotionAsync(_fasteningGantry.ReturnFromPickupAsync, cancellationToken);

    private bool CanReturnFromPickup() =>
        SelectedMotionGroup == MotionGroup.BoltFastening && CanUseCurrentHandler();

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
        point switch
        {
            { Target: TeachingTarget.BoltPickup } =>
                _fasteningGantry.MoveToPickupPositionAsync(cancellationToken),
            { TeachMode: TeachMode.XYOnly } => _fasteningGantry.MoveToXYAsync(
                point.X,
                point.Y,
                cancellationToken),
            { TeachMode: TeachMode.ZOnly } =>
                _fasteningGantry.MoveZAsync(
                    point.Z!.Value,
                    cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(point)),
        };

    private bool CanMoveToPoint() =>
        SelectedPoint is not null
        && CanUseCurrentHandler()
        && (SelectedPoint.TeachMode == TeachMode.ZOnly || CanMoveHorizontal())
        && SelectedPoint.Position.HasPosition;

    private bool CanMoveHorizontal() => SelectedMotionGroup switch
    {
        MotionGroup.PcbPlacementHandler => _placementHandler.CanMoveHorizontal,
        MotionGroup.BoltFastening => _fasteningGantry.CanMoveHorizontal,
        MotionGroup.InspectionGantry => _inspectionGantry.CanMove,
        _ => false,
    };

    protected override bool CanUseCurrentHandler() =>
        _state.ManualControlsEnabled
        && (SelectedMotionGroup != MotionGroup.PcbPlacementHandler
            || !_buffer.SupplyInside);

    protected override void NotifyManualTeachingCommands()
    {
        OnPropertyChanged(nameof(CanEditInspectionRecipe));
        if (IsCameraLive && (!_state.ManualMode || !_state.SafetyReady || _state.IsError))
        {
            StopCamera();
        }
        NotifyMotionCommands();
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        MoveToPointCommand.NotifyCanExecuteChanged();
        ReturnFromPickupCommand.NotifyCanExecuteChanged();
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        CaptureCarrierImagesCommand.NotifyCanExecuteChanged();
        CaptureInspectionCommand.NotifyCanExecuteChanged();
        ReinspectImageCommand.NotifyCanExecuteChanged();
        CollectBoltImagesCommand.NotifyCanExecuteChanged();
        TeachImageRegionCommand.NotifyCanExecuteChanged();
        TeachImagePointCommand.NotifyCanExecuteChanged();
        AddBoltPointCommand.NotifyCanExecuteChanged();
        RemoveBoltPointCommand.NotifyCanExecuteChanged();
    }

}
