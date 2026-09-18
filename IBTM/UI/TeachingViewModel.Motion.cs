using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;

namespace IBTM.UI;

public partial class TeachingViewModel
{
    public string HorizontalZName
    {
        get
        {
            return ActiveMotionGroup switch
            {
                MotionGroup.PcbSupply => "Transport / Rotation Z",
                MotionGroup.PcbPlacementHandler => "Handoff / Travel Z",
                _ => "Safe Z",
            };
        }
    }

    public override TeachingMotionHint MotionHint
    {
        get
        {
            if (!State.Display.Available)
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
            if (ActiveTeachingUnit == HardwareArea.NgCarrierTransfer
                && _ngTransferSettings.PickupSafeX is null)
                return TeachingMotionHint.NgPickupSafeXRequired;
            return ActiveMotionGroup switch
            {
                MotionGroup.PcbSupply when State.Display.SupplyInBufferArea
                    => TeachingMotionHint.SupplyInBufferRestricted,
                MotionGroup.PcbSupply when CanEditTeaching && !CanJog(MotionAxis.X)
                    => TeachingMotionHint.SafeZRequired,
                MotionGroup.PcbPlacementHandler when !_placementHandler.HandlerRaised
                    => TeachingMotionHint.RaisePlacementCylinders,
                MotionGroup.BoltFastening => TeachingMotionHint.BoltAdjustment,
                MotionGroup.InspectionGantry when !_ngTransfer.IsRaised
                    => TeachingMotionHint.RaiseNgPickup,
                MotionGroup.PcbPlacementHandler when !Motion.IsAtZ(_placementSettings.BufferHandoffPosition.Z)
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
            return ActiveTeachingUnit == HardwareArea.NgCarrierTransfer
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
                HardwareArea.PcbSupply => MotionGroup.PcbSupply,
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
                MotionGroup.PcbSupply => _supplyHandler.CanJog(axis, live: false),
                MotionGroup.PcbPlacementHandler => _placementHandler.CanJog(axis, live: false),
                MotionGroup.BoltFastening => _fasteningGantry.CanJog(axis),
                MotionGroup.InspectionGantry => _inspectionGantry.CanJog(axis),
                _ => false,
            };
    }

    protected override async Task JogAsync(TeachingDirection direction, CancellationToken cancellationToken)
    {
        var group = ActiveMotionGroup;
        var (axis, sign) = Resolve(direction);
        var velocity = sign * JogSpeed;
        var viewCancellation = ViewCancellation;
        var activeCancellation = cancellationToken;
        try
        {
            if (!Machine.CanUseManualMotion(group))
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(group),
                cancellationToken,
                viewCancellation);
            if (operation is null)
                return;
            activeCancellation = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            await (group switch
            {
                MotionGroup.PcbSupply => _supplyHandler.JogAsync(axis, velocity, operation.Token),
                MotionGroup.PcbPlacementHandler => _placementHandler.JogAsync(axis, velocity, operation.Token),
                MotionGroup.BoltFastening => _fasteningGantry.JogAsync(axis, velocity, operation.Token),
                MotionGroup.InspectionGantry => _inspectionGantry.JogAsync(axis, velocity, operation.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(group)),
            });
        }
        catch (OperationCanceledException) when (activeCancellation.IsCancellationRequested
            || viewCancellation.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(Machine.GetMotionAlarm(group), exception);
        }
    }

    protected override async Task MoveToHorizontalZAsync(CancellationToken cancellationToken)
    {
        var commandGroup = ActiveMotionGroup;
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (!Machine.CanUseManualMotion(commandGroup))
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            await (commandGroup switch
            {
                MotionGroup.PcbSupply => _supplyHandler.MoveToRotationZAsync(operation.Token),
                MotionGroup.PcbPlacementHandler => _placementHandler.MoveToHorizontalZAsync(operation.Token),
                MotionGroup.BoltFastening => _fasteningGantry.MoveToSafeZAsync(operation.Token),
                MotionGroup.InspectionGantry => Task.CompletedTask,
                _ => throw new ArgumentOutOfRangeException(nameof(ActiveMotionGroup)),
            });
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(Machine.GetMotionAlarm(commandGroup), exception);
        }
    }

    protected override async Task StepAsync(TeachingDirection direction, CancellationToken cancellationToken)
    {
        var commandGroup = ActiveMotionGroup;
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (!Machine.CanUseManualMotion(commandGroup))
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            var (axis, target) = GetStepTarget(direction, Motion.Feedback.GetPosition());
            await (commandGroup switch
            {
                MotionGroup.PcbSupply => _supplyHandler.MoveAxisAsync(axis, target, operation.Token),
                MotionGroup.PcbPlacementHandler => _placementHandler.MoveAxisAsync(axis, target, operation.Token),
                MotionGroup.BoltFastening => _fasteningGantry.AdjustAxisAsync(axis, target, JogSpeed, operation.Token),
                MotionGroup.InspectionGantry => _inspectionGantry.MoveAxisAsync(axis, target, TeachingXySpeed, operation.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(ActiveMotionGroup)),
            });
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(Machine.GetMotionAlarm(commandGroup), exception);
        }
    }

    [RelayCommand(CanExecute = nameof(CanReturnFromPickup))]
    private async Task ReturnFromPickupAsync(CancellationToken cancellationToken)
    {
        var commandGroup = ActiveMotionGroup;
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (!Machine.CanUseManualMotion(commandGroup))
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            await _fasteningGantry.ReturnFromPickupAsync(operation.Token);
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(Machine.GetMotionAlarm(commandGroup), exception);
        }
    }

    private bool CanReturnFromPickup()
    {
        return ActiveMotionGroup == MotionGroup.BoltFastening
            && Machine.CanUseManualMotion(ActiveMotionGroup, live: false);
    }

    protected override async Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint!;
        var commandGroup = ActiveMotionGroup;
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (!Machine.CanUseManualMotion(commandGroup))
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            await (point.Position.MotionGroup switch
            {
                MotionGroup.PcbSupply => _supplyHandler.MoveToTeachingPositionAsync(point.Position, point.Read(), operation.Token),
                MotionGroup.PcbPlacementHandler => _placementHandler.MoveToTeachingPositionAsync(point.Position, point.Read(), operation.Token),
                MotionGroup.BoltFastening => _fasteningGantry.MoveToTeachingPositionAsync(point.Position, point.Read(), operation.Token),
                MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.NgPickupSafeX => _inspectionGantry.MoveAxisAsync(MotionAxis.X, point.X, TeachingXySpeed, operation.Token),
                MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.NgCarrierPickup => _ngCarrierMove.MoveToCarrierAsync(NgTransferDestination.Station, operation.Token),
                MotionGroup.InspectionGantry when point.Position.Bolt is { } bolt => Inspector.MoveToAsync(bolt, operation.Token),
                MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.DataMatrix => Inspector.MoveToBarcodeAsync(SelectedPcb, operation.Token),
                MotionGroup.InspectionGantry => _inspectionGantry.MoveToAsync(new AxisPosition { X = point.X, Y = point.Y }, TeachingXySpeed, operation.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(point)),
            });
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            Machine.ReportManualFailure(Machine.GetMotionAlarm(commandGroup), exception);
        }
    }

    protected override bool CanMoveToPoint()
    {
        if (ActiveMotionGroup == MotionGroup.PcbSupply)
        {
            return SelectedPoint is { } point
                && Machine.CanUseManualMotion(ActiveMotionGroup, live: false)
                && _supplyHandler.CanMoveToTeachingPosition(point.Position, live: false);
        }

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
            MotionGroup.PcbPlacementHandler => _placementHandler.HandlerRaised,
            MotionGroup.BoltFastening => _fasteningGantry.CanMoveHorizontal,
            MotionGroup.InspectionGantry => _ngTransfer.IsRaised,
            _ => false,
        };
    }

    protected override void NotifyManualTeachingCommands()
    {
        OnPropertyChanged(nameof(HomeBlock));
        OnPropertyChanged(nameof(CanEditInspectionRecipe));
        if (!State.ManualMode && (Inspector.IsLiveView || ToggleLiveViewCommand.IsRunning))
        {
            _ = RequestCameraStopAsync();
        }

        NotifyMotionCommands();
        SaveHandoffSetupCommand.NotifyCanExecuteChanged();
        ReturnFromPickupCommand.NotifyCanExecuteChanged();
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        CaptureCarrierImageCommand.NotifyCanExecuteChanged();
        ApplyRulerResolutionCommand.NotifyCanExecuteChanged();
        DrawFovRegionCommand.NotifyCanExecuteChanged();
        TeachFovRegionCommand.NotifyCanExecuteChanged();
        CaptureInspectionCommand.NotifyCanExecuteChanged();
        ReinspectImageCommand.NotifyCanExecuteChanged();
        AddBoltPointCommand.NotifyCanExecuteChanged();
        RemoveBoltPointCommand.NotifyCanExecuteChanged();
    }
}
