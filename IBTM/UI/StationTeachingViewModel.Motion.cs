using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;

namespace IBTM.UI;

public partial class StationTeachingViewModel
{
    public override TeachingMotionHint MotionHint
    {
        get
        {
            if (!_state.Display.Available)
                return IsInspectionSelected ? TeachingMotionHint.None : TeachingMotionHint.MotionUnavailable;
            if (!IsInspectionSelected)
            {
                if (HomeBlock == HomeBlockReason.UnitDisabled)
                    return TeachingMotionHint.UnitDisabled;
                if (Motion.Axes.Values.Any(axis => axis.State is null))
                    return TeachingMotionHint.MotionUnavailable;
                if (Motion.Axes.Values.Any(axis => axis.State is { Alarm: true } or { Emergency: true }))
                    return TeachingMotionHint.AxisFault;
                if (Motion.Axes.Values.Any(axis => axis.State is { ServoOn: false }))
                    return TeachingMotionHint.ServoOff;
                if (Motion.Axes.Values.Any(axis => axis.State is { Homed: false }))
                    return TeachingMotionHint.HomeRequired;
            }
            if (SelectedTeachingUnit == HardwareArea.NgCarrierTransfer
                && _ngTransferSettings.PickupSafeX is null)
                return TeachingMotionHint.NgPickupSafeXRequired;
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

    public HomeBlockReason HomeBlock
    {
        get
        {
            return IsInspectionSelected ? HomeBlockReason.None : Machine.GetHomeBlock(ActiveMotionGroup);
        }
    }

    private double TeachingXySpeed
    {
        get
        {
            return SelectedTeachingUnit == HardwareArea.NgCarrierTransfer
                ? _ngTransferSettings.Speed
                : _inspectionGantrySettings.Motion.HorizontalSpeed;
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
                        TeachingXySpeed,
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
                MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.NgPickupSafeX
                    => _inspectionGantry.MoveAxisAsync(MotionAxis.X, point.X, TeachingXySpeed, token),
                MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.NgCarrierPickup
                    => _ngCarrierMove.MoveToCarrierAsync(NgTransferDestination.Station, token),
                MotionGroup.InspectionGantry when point.Position.Bolt is { } bolt
                    => Inspector.MoveToAsync(bolt, token),
                MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.DataMatrix
                    => Inspector.MoveToBarcodeAsync(SelectedPcb, token),
                MotionGroup.InspectionGantry => _inspectionGantry.MoveToAsync(
                    new AxisPosition { X = point.X, Y = point.Y },
                    TeachingXySpeed,
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
            && (SelectedPoint.Position.Target != TeachingTarget.NgCarrierPickup
                || _ngTransferSettings.PickupSafeX is not null)
            && (IsInspectionSelected && SelectedPoint.Position.Bolt is { } bolt
                ? Inspector.HasPosition(bolt)
                : SelectedPoint.Position.HasPosition);
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
        OnPropertyChanged(nameof(HomeBlock));
        OnPropertyChanged(nameof(CanEditInspectionRecipe));
        if (!_state.ManualMode && (Inspector.IsLiveView || ToggleLiveViewCommand.IsRunning))
        {
            _ = RequestCameraStopAsync();
        }

        NotifyMotionCommands();
        ReturnFromPickupCommand.NotifyCanExecuteChanged();
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        CaptureCarrierImageCommand.NotifyCanExecuteChanged();
        ClearCarrierImagesCommand.NotifyCanExecuteChanged();
        DrawFovRegionCommand.NotifyCanExecuteChanged();
        TeachFovRegionCommand.NotifyCanExecuteChanged();
        CaptureInspectionCommand.NotifyCanExecuteChanged();
        ReinspectImageCommand.NotifyCanExecuteChanged();
        CollectBoltImagesCommand.NotifyCanExecuteChanged();
        AddBoltPointCommand.NotifyCanExecuteChanged();
        RemoveBoltPointCommand.NotifyCanExecuteChanged();
    }

}
