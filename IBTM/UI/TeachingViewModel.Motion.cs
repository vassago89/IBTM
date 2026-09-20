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
    public string MoveToHorizontalZLabel
    {
        get
        {
            switch (ActiveMotionGroup)
            {
                case MotionGroup.PcbSupply:
                    return "Move Z to Rotation Height";
                case MotionGroup.PcbPlacementHandler:
                    return "Move Z to Standby Height";
                default:
                    return "Move Z to Travel Height";
            }
        }
    }

    public TeachingMotionHint MotionHint
    {
        get
        {
            if (!State.Available)
                return IsInspectionSelected ? TeachingMotionHint.None : TeachingMotionHint.MotionUnavailable;
            if (!IsInspectionSelected)
            {
                switch (true)
                {
                    case true when HomeBlock == HomeBlockReason.UnitDisabled:
                        return TeachingMotionHint.UnitDisabled;
                    case true when Motion.Axes.Values.Any(axis => axis.State is null):
                        return TeachingMotionHint.MotionUnavailable;
                    case true when Motion.Axes.Values.Any(axis => axis.State is { Alarm: true } or { Emergency: true }):
                        return TeachingMotionHint.AxisFault;
                    case true when Motion.Axes.Values.Any(axis => axis.State is { ServoOn: false }):
                        return TeachingMotionHint.ServoOff;
                    case true when Motion.Axes.Values.Any(axis => axis.State is { Homed: false }):
                        return TeachingMotionHint.HomeRequired;
                }
            }
            if (SelectedTeachingUnit == HardwareArea.NgCarrierTransfer
                && _ngTransferSettings.PickupSafeX is null)
                return TeachingMotionHint.NgPickupSafeXRequired;
            switch (ActiveMotionGroup)
            {
                case MotionGroup.PcbPlacementHandler when !_placementHandler.HandlerRaised:
                    return TeachingMotionHint.RaisePlacementCylinders;
                case MotionGroup.BoltFastening:
                    return TeachingMotionHint.BoltAdjustment;
                case MotionGroup.InspectionGantry when !_ngTransfer.IsRaised:
                    return TeachingMotionHint.RaiseNgPickup;
                default:
                    return TeachingMotionHint.None;
            }
        }
    }

    public HomeBlockReason HomeBlock => IsInspectionSelected ? HomeBlockReason.None : Machine.GetHomeBlock(ActiveMotionGroup);

    private double TeachingXySpeed
    {
        get
        {
            return SelectedTeachingUnit == HardwareArea.NgCarrierTransfer
                ? _ngTransferSettings.Speed
                : _inspectionGantrySettings.Motion.HorizontalSpeed;
        }
    }

    public MotionGroup ActiveMotionGroup
    {
        get
        {
            switch (SelectedTeachingUnit)
            {
                case HardwareArea.PcbSupply:
                    return MotionGroup.PcbSupply;
                case HardwareArea.PcbPlacementHandler:
                    return MotionGroup.PcbPlacementHandler;
                case HardwareArea.BoltFastening:
                    return MotionGroup.BoltFastening;
                case HardwareArea.InspectionGantry or HardwareArea.NgCarrierTransfer:
                    return MotionGroup.InspectionGantry;
                default:
                    throw new ArgumentOutOfRangeException(nameof(SelectedTeachingUnit));
            }
        }
    }

    private bool IsJogAllowed(MotionAxis axis)
    {
        return !State.IsRunning
            && Machine.IsManualMotionReady(ActiveMotionGroup, live: false)
            && Motion.Feedback.Axes.Contains(axis)
            && ActiveMotionGroup switch
            {
                MotionGroup.PcbSupply or MotionGroup.BoltFastening => true,
                MotionGroup.PcbPlacementHandler => axis == MotionAxis.Z || _placementHandler.HandlerRaised,
                MotionGroup.InspectionGantry => _ngTransfer.IsRaised,
                _ => false,
            };
    }

    public IAsyncRelayCommand<TeachingDirection> JogCommand { get; }

    private async Task JogAsync(TeachingDirection direction, CancellationToken cancellationToken)
    {
        var group = ActiveMotionGroup;
        var (axis, sign) = Resolve(direction);
        var velocity = sign * JogSpeed;
        var viewCancellation = ViewCancellation;
        var activeCancellation = cancellationToken;
        try
        {
            if (State.IsRunningFor())
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(group),
                cancellationToken,
                viewCancellation);
            if (operation is null)
                return;
            activeCancellation = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            switch (group)
            {
                case MotionGroup.PcbSupply:
                    await _supplyHandler.JogAsync(axis, velocity, operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _placementHandler.JogAsync(axis, velocity, operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningGantry.JogAsync(axis, velocity, operation.Token);
                    break;
                case MotionGroup.InspectionGantry:
                    await _inspectionGantry.JogAsync(axis, velocity, operation.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(group));
            }
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

    public IAsyncRelayCommand MoveToHorizontalZCommand { get; }

    private async Task MoveToHorizontalZAsync(CancellationToken cancellationToken)
    {
        var commandGroup = ActiveMotionGroup;
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (State.IsRunningFor())
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            switch (commandGroup)
            {
                case MotionGroup.PcbSupply:
                    await _supplyHandler.MoveToRotationZAsync(operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _placementHandler.MoveToHorizontalZAsync(operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningGantry.MoveToSafeZAsync(operation.Token);
                    break;
                case MotionGroup.InspectionGantry:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ActiveMotionGroup));
            }
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

    public IAsyncRelayCommand<TeachingDirection> StepCommand { get; }

    private async Task StepAsync(TeachingDirection direction, CancellationToken cancellationToken)
    {
        var commandGroup = ActiveMotionGroup;
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (State.IsRunningFor())
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            var current = Motion.Feedback.GetPosition();
            var (axis, sign) = Resolve(direction);
            var position = axis switch
            {
                MotionAxis.X => current.X,
                MotionAxis.Y => current.Y,
                MotionAxis.Z => current.Z,
                _ => throw new ArgumentOutOfRangeException(nameof(direction)),
            };
            var target = position + sign * StepDistance;
            switch (commandGroup)
            {
                case MotionGroup.PcbSupply:
                    await _supplyHandler.AdjustAxisAsync(axis, target, JogSpeed, operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _placementHandler.AdjustAxisAsync(axis, target, JogSpeed, operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningGantry.AdjustAxisAsync(axis, target, JogSpeed, operation.Token);
                    break;
                case MotionGroup.InspectionGantry:
                    await _inspectionGantry.MoveAxisAsync(axis, target, TeachingXySpeed, operation.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ActiveMotionGroup));
            }
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

    public IAsyncRelayCommand ReturnFromPickupCommand { get; }

    private async Task ReturnFromPickupAsync(CancellationToken cancellationToken)
    {
        var commandGroup = ActiveMotionGroup;
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (State.IsRunningFor())
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

    private bool IsReturnFromPickupAllowed
    {
        get
        {
            return ActiveMotionGroup == MotionGroup.BoltFastening
                && !State.IsRunning
                && Machine.IsManualMotionReady(ActiveMotionGroup, live: false);
        }
    }

    public IAsyncRelayCommand MoveToPointCommand { get; }

    private async Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint!;
        var commandGroup = ActiveMotionGroup;
        var viewToken = ViewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (State.IsRunningFor())
                return;
            using var operation = Machine.BeginManualOperation(
                () => Machine.IsManualMotionReady(commandGroup),
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            switch (point.Position.MotionGroup)
            {
                case MotionGroup.PcbSupply:
                    await _supplyHandler.MoveToTeachingPositionAsync(point.Position, point.Read(), operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _placementHandler.MoveToTeachingPositionAsync(point.Position, point.Read(), operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningGantry.MoveToTeachingPositionAsync(point.Position, point.Read(), operation.Token);
                    break;
                case MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.NgPickupSafeX:
                    await _inspectionGantry.MoveAxisAsync(MotionAxis.X, point.X, TeachingXySpeed, operation.Token);
                    break;
                case MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.NgCarrierPickup:
                    await _ngCarrierMove.MoveToCarrierAsync(NgTransferDestination.Station, operation.Token);
                    break;
                case MotionGroup.InspectionGantry when point.Position.Bolt is { } bolt:
                    await Inspector.MoveToAsync(bolt, operation.Token);
                    break;
                case MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.DataMatrix:
                    await Inspector.MoveToBarcodeAsync(SelectedPcb, operation.Token);
                    break;
                case MotionGroup.InspectionGantry:
                    await _inspectionGantry.MoveToAsync(new AxisPosition { X = point.X, Y = point.Y }, TeachingXySpeed, operation.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(point));
            }
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

    private bool IsMoveToPointAllowed
    {
        get
        {
            switch (SelectedPoint)
            {
                case null:
                    return false;
                case { } when State.IsRunning
                    || !Machine.IsManualMotionReady(ActiveMotionGroup, live: false):
                    return false;
                case { } when ActiveMotionGroup == MotionGroup.PcbPlacementHandler
                    && !_placementHandler.HandlerRaised:
                    return false;
                case { } point when ActiveMotionGroup == MotionGroup.PcbSupply:
                    return _supplyHandler.IsMoveToTeachingPositionAllowed(point.Position);
                case { } point:
                    return (point.Position.Mode == TeachMode.ZOnly || IsHorizontalMoveAllowed)
                        && (point.Position.Target != TeachingTarget.NgCarrierPickup
                            || _ngTransferSettings.PickupSafeX is not null)
                        && (IsInspectionSelected && point.Position.Bolt is { } bolt
                            ? Inspector.HasPosition(bolt)
                            : point.Position.HasPosition);
            }
        }
    }

    private bool IsHorizontalMoveAllowed
    {
        get
        {
            switch (ActiveMotionGroup)
            {
                case MotionGroup.PcbPlacementHandler:
                    return _placementHandler.HandlerRaised;
                case MotionGroup.BoltFastening:
                    return _fasteningGantry.IsHorizontalMoveAllowed;
                case MotionGroup.InspectionGantry:
                    return _ngTransfer.IsRaised;
                default:
                    return false;
            }
        }
    }

    private void NotifyManualTeachingCommands()
    {
        OnPropertyChanged(nameof(HomeBlock));
        if (!State.ManualMode && (Inspector.IsLiveView || ToggleLiveViewCommand.IsRunning))
        {
            _ = RequestCameraStopAsync();
        }

        NotifyMotionCommands();
        SaveCommand.NotifyCanExecuteChanged();
        ReturnFromPickupCommand.NotifyCanExecuteChanged();
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        ApplyRulerResolutionCommand.NotifyCanExecuteChanged();
        DrawFovRegionCommand.NotifyCanExecuteChanged();
        TeachFovRegionCommand.NotifyCanExecuteChanged();
        CaptureInspectionCommand.NotifyCanExecuteChanged();
        ReinspectImageCommand.NotifyCanExecuteChanged();
        AddBoltPointCommand.NotifyCanExecuteChanged();
        RemoveBoltPointCommand.NotifyCanExecuteChanged();
    }
}
