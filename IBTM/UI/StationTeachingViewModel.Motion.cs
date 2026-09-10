using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class StationTeachingViewModel
{
    public override TeachingMotionHint MotionHint
    {
        get
        {
            if (!_state.Display.Available)
                return TeachingMotionHint.None;
            return ActiveMotionGroup switch
            {
                MotionGroup.PcbPlacementHandler when _state.Display.SupplyInBufferArea
                    => TeachingMotionHint.SupplyInBuffer,
                MotionGroup.PcbPlacementHandler when !_placementHandler.CanMoveHorizontal
                    => TeachingMotionHint.RaisePlacementCylinders,
                MotionGroup.BoltFastening => TeachingMotionHint.BoltAdjustment,
                MotionGroup.InspectionGantry when !_inspectionGantry.CanMove
                    => TeachingMotionHint.RaiseNgPickup,
                MotionGroup.PcbPlacementHandler when !Motion.IsAtZ(_placementSettings.BufferEntryZ)
                    => TeachingMotionHint.SafeZRequired,
                _ => TeachingMotionHint.None,
            };
        }
    }

    public override MotionGroup ActiveMotionGroup
    {
        get
        {
            return SelectedTeachingUnit switch
            {
                HardwareArea.PcbPlacementHandler => MotionGroup.PcbPlacementHandler,
                HardwareArea.BoltFastening => MotionGroup.BoltFastening,
                HardwareArea.InspectionGantry or HardwareArea.NgCarrierTransfer
                    => MotionGroup.InspectionGantry,
                _ => throw new ArgumentOutOfRangeException(nameof(SelectedTeachingUnit)),
            };
        }
    }

    public override HardwareArea ActiveTeachingUnit
    {
        get
        {
            return SelectedTeachingUnit;
        }
    }

    protected override bool CanJog(MotionAxis axis)
    {
        return Machine.CanUseManualMotion(ActiveMotionGroup, live: false)
            && ActiveMotionGroup switch
            {
                MotionGroup.PcbPlacementHandler => _placementHandler.CanJog(axis, live: false),
                MotionGroup.BoltFastening => _fasteningGantry.CanJog(axis),
                MotionGroup.InspectionGantry => _inspectionGantry.CanJog(axis),
                _ => false,
            };
    }

    protected override Task JogAsync(TeachingDirection direction, CancellationToken cancellationToken)
    {
        var (axis, sign) = Resolve(direction);
        return Machine.RunManualMotionAsync(
            ActiveMotionGroup,
            token => ActiveMotionGroup switch
            {
                MotionGroup.PcbPlacementHandler
                    => _placementHandler.JogAsync(axis, sign * JogSpeed, token),
                MotionGroup.BoltFastening
                    => _fasteningGantry.JogAsync(axis, sign * JogSpeed, token),
                MotionGroup.InspectionGantry
                    => _inspectionGantry.JogAsync(axis, sign * JogSpeed, token),
                _ => throw new ArgumentOutOfRangeException(nameof(ActiveMotionGroup)),
            },
            cancellationToken,
            ViewCancellation);
    }

    protected override Task MoveToHorizontalZAsync(CancellationToken cancellationToken)
    {
        return Machine.RunManualMotionAsync(
            ActiveMotionGroup,
            token => ActiveMotionGroup switch
            {
                MotionGroup.PcbPlacementHandler => _placementHandler.MoveToHorizontalZAsync(token),
                MotionGroup.BoltFastening => _fasteningGantry.MoveToSafeZAsync(token),
                MotionGroup.InspectionGantry => Task.CompletedTask,
                _ => throw new ArgumentOutOfRangeException(nameof(ActiveMotionGroup)),
            },
            cancellationToken,
            ViewCancellation);
    }

    protected override Task StepAsync(TeachingDirection direction, CancellationToken cancellationToken)
    {
        return Machine.RunManualMotionAsync(
            ActiveMotionGroup,
            token =>
            {
                var (axis, target) = StepTarget(direction, Motion.Feedback.GetPosition());
                return ActiveMotionGroup switch
                {
                    MotionGroup.PcbPlacementHandler
                        => _placementHandler.MoveAxisAsync(axis, target, token),
                    MotionGroup.BoltFastening
                        => _fasteningGantry.AdjustAxisAsync(axis, target, JogSpeed, token),
                    MotionGroup.InspectionGantry => _inspectionGantry.MoveAxisAsync(
                        axis,
                        target,
                        _inspectionGantrySettings.Motion.HorizontalSpeed,
                        token),
                    _ => throw new ArgumentOutOfRangeException(nameof(ActiveMotionGroup)),
                };
            },
            cancellationToken,
            ViewCancellation);
    }

    [RelayCommand(CanExecute = nameof(CanReturnFromPickup))]
    private Task ReturnFromPickupAsync(CancellationToken cancellationToken)
    {
        return Machine.RunManualMotionAsync(
            ActiveMotionGroup,
            _fasteningGantry.ReturnFromPickupAsync,
            cancellationToken,
            ViewCancellation);
    }

    private bool CanReturnFromPickup()
    {
        return ActiveMotionGroup == MotionGroup.BoltFastening
            && Machine.CanUseManualMotion(ActiveMotionGroup, live: false);
    }

    protected override Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint!;
        return Machine.RunManualMotionAsync(
            ActiveMotionGroup,
            token => point.Position.MotionGroup switch
            {
                MotionGroup.PcbPlacementHandler => _placementHandler.MoveToTeachingPositionAsync(
                    point.Position,
                    point.Read(),
                    token),
                MotionGroup.BoltFastening => _fasteningGantry.MoveToTeachingPositionAsync(
                    point.Position,
                    point.Read(),
                    token),
                MotionGroup.InspectionGantry => _inspectionGantry.MoveToAsync(
                    new AxisPosition { X = point.X, Y = point.Y },
                    _inspectionGantrySettings.Motion.HorizontalSpeed,
                    token),
                _ => throw new ArgumentOutOfRangeException(nameof(point)),
            },
            cancellationToken,
            ViewCancellation);
    }

    protected override bool CanMoveToPoint()
    {
        return SelectedPoint is not null
            && Machine.CanUseManualMotion(ActiveMotionGroup, live: false)
            && (SelectedPoint.Position.Mode == TeachMode.ZOnly || CanMoveHorizontal())
            && SelectedPoint.Position.HasPosition;
    }

    private bool CanMoveHorizontal()
    {
        return ActiveMotionGroup switch
        {
            MotionGroup.PcbPlacementHandler => _placementHandler.CanMoveHorizontal,
            MotionGroup.BoltFastening => _fasteningGantry.CanMoveHorizontal,
            MotionGroup.InspectionGantry => _inspectionGantry.CanMove,
            _ => false,
        };
    }

    protected override void NotifyManualTeachingCommands()
    {
        OnPropertyChanged(nameof(CanEditInspectionRecipe));
        if (!_state.ManualMode && (Inspector.IsLiveView || ToggleLiveViewCommand.IsRunning))
        {
            _ = RequestCameraStopAsync();
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
