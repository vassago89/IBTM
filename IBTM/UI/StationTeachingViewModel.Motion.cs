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
        MotionGroup.PcbPlacementHandler when _state.Display.SupplyInBufferArea =>
            TeachingMotionHint.SupplyInBuffer,
        MotionGroup.PcbPlacementHandler when !_placementHandler.CanMoveHorizontal =>
            TeachingMotionHint.RaisePlacementCylinders,
        MotionGroup.BoltFastening => TeachingMotionHint.BoltAdjustment,
        MotionGroup.InspectionGantry when !_inspectionGantry.CanMove =>
            TeachingMotionHint.RaiseNgPickup,
        MotionGroup.PcbPlacementHandler when !Motion.IsAtZ(_placementSettings.BufferEntryZ) =>
            TeachingMotionHint.SafeZRequired,
        _ => TeachingMotionHint.None,
    };

    protected override MotionGroup CurrentMotionGroup =>
        SelectedMotionGroup;

    protected override bool CanJog(MotionAxis axis) =>
        CanUseCurrentHandler()
        && SelectedMotionGroup switch
        {
            MotionGroup.PcbPlacementHandler => _placementHandler.CanJog(axis, live: false),
            MotionGroup.BoltFastening => _fasteningGantry.CanJog(axis),
            MotionGroup.InspectionGantry => _inspectionGantry.CanJog(axis),
            _ => false,
        };

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
        var current = CurrentFeedback.GetPosition();
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

    [RelayCommand(CanExecute = nameof(CanReturnFromPickup))]
    private Task ReturnFromPickupAsync(CancellationToken cancellationToken) =>
        RunMotionAsync(_fasteningGantry.ReturnFromPickupAsync, cancellationToken);

    private bool CanReturnFromPickup() =>
        SelectedMotionGroup == MotionGroup.BoltFastening && CanUseCurrentHandler();

    protected override Task MovePointAsync(
        TeachingPoint point,
        CancellationToken cancellationToken) =>
        point.MotionGroup switch
        {
            MotionGroup.PcbPlacementHandler =>
                _placementHandler.MoveToTeachingPositionAsync(point.Position, point.Read(), cancellationToken),
            MotionGroup.BoltFastening =>
                _fasteningGantry.MoveToTeachingPositionAsync(point.Position, point.Read(), cancellationToken),
            MotionGroup.InspectionGantry =>
                _inspectionGantry.MoveToAsync(
                    new AxisPosition { X = point.X, Y = point.Y },
                    _inspectionGantrySettings.Motion.HorizontalSpeed,
                    cancellationToken),
            _ => throw new ArgumentOutOfRangeException(),
        };

    protected override bool CanMoveToPoint() =>
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

    protected override void NotifyManualTeachingCommands()
    {
        OnPropertyChanged(nameof(CanEditInspectionRecipe));
        if (IsCameraLive && (!_state.ManualMode || !_state.SafetyReady || _state.IsError))
        {
            StopCamera();
        }
        NotifyMotionCommands();
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
