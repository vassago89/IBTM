using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using Microsoft.Extensions.Logging;

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
                    return "Move Z to Safe Z";
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
            if (SelectedPoint?.Position.Target == TeachingTarget.NgCarrierPickup
                && _settings.NgCarrierTransfer.PickupSafeX is null)
                return TeachingMotionHint.NgPickupPositionRequired;
            switch (ActiveMotionGroup)
            {
                case MotionGroup.PcbPlacementHandler when !_pcbPlacement.HandlerRaised:
                    return TeachingMotionHint.RaisePlacementCylinders;
                case MotionGroup.BoltFastening:
                    return TeachingMotionHint.BoltAdjustment;
                case MotionGroup.InspectionGantry when !Inspection.IsRaised:
                    return TeachingMotionHint.RaiseNgPickup;
                default:
                    return TeachingMotionHint.None;
            }
        }
    }

    public HomeBlockReason HomeBlock => IsInspectionSelected ? HomeBlockReason.None : Machine.GetHomeBlock(ActiveMotionGroup);

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
                case HardwareArea.InspectionGantry:
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
                MotionGroup.PcbPlacementHandler => axis == MotionAxis.Z || _pcbPlacement.HandlerRaised,
                MotionGroup.InspectionGantry => Inspection.IsRaised,
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
            SaveError = null;
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
                    await _pcbSupply.JogAsync(axis, velocity, operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _pcbPlacement.JogAsync(axis, velocity, operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningStation.JogAsync(axis, velocity, operation.Token);
                    break;
                case MotionGroup.InspectionGantry:
                    await Inspection.JogAsync(axis, velocity, operation.Token);
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
        catch (ArgumentException exception)
        {
            SaveError = $"Motion settings or target are invalid: {exception.Message}";
            _logger.LogWarning(exception, "Teaching motion rejected invalid settings or target.");
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
            SaveError = null;
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
                    await _pcbSupply.MoveToRotationZAsync(operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _pcbPlacement.MoveToHorizontalZAsync(operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningStation.MoveToSafeZAsync(operation.Token);
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
        catch (ArgumentException exception)
        {
            SaveError = $"Motion settings or target are invalid: {exception.Message}";
            _logger.LogWarning(exception, "Teaching motion rejected invalid settings or target.");
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
            SaveError = null;
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
                    await _pcbSupply.AdjustAxisAsync(axis, target, JogSpeed, operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _pcbPlacement.AdjustAxisAsync(axis, target, JogSpeed, operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningStation.AdjustAxisAsync(axis, target, JogSpeed, operation.Token);
                    break;
                case MotionGroup.InspectionGantry:
                    await Inspection.MoveAxisAsync(axis, target, _settings.InspectionGantry.Motion.HorizontalSpeed, operation.Token);
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
        catch (ArgumentException exception)
        {
            SaveError = $"Motion settings or target are invalid: {exception.Message}";
            _logger.LogWarning(exception, "Teaching motion rejected invalid settings or target.");
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
            SaveError = null;
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
            await _fasteningStation.ReturnFromPickupAsync(operation.Token);
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || Operations.IsShuttingDown)
        {
        }
        catch (ArgumentException exception)
        {
            SaveError = $"Motion settings or target are invalid: {exception.Message}";
            _logger.LogWarning(exception, "Teaching motion rejected invalid settings or target.");
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
            SaveError = null;
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
                    await _pcbSupply.MoveToTeachingPositionAsync(point.Position, point.Read(), operation.Token);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    await _pcbPlacement.MoveToTeachingPositionAsync(point.Position, point.Read(), operation.Token);
                    break;
                case MotionGroup.BoltFastening:
                    await _fasteningStation.MoveToTeachingPositionAsync(point.Position, point.Read(), operation.Token);
                    break;
                case MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.NgCarrierPickup:
                    await Inspection.MoveToCarrierAsync(NgTransferDestination.Station, operation.Token);
                    break;
                case MotionGroup.InspectionGantry when point.Position.Bolt is { } bolt:
                    await Inspection.MoveToBoltAsync(bolt, operation.Token);
                    break;
                case MotionGroup.InspectionGantry when point.Position.Target == TeachingTarget.DataMatrix:
                    await Inspection.MoveToBarcodeAsync(SelectedPcb, operation.Token);
                    break;
                case MotionGroup.InspectionGantry:
                    await Inspection.MoveToAsync(new AxisPosition { X = point.X, Y = point.Y }, cancellationToken: operation.Token);
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
        catch (ArgumentException exception)
        {
            SaveError = $"Motion settings or target are invalid: {exception.Message}";
            _logger.LogWarning(exception, "Teaching motion rejected invalid settings or target.");
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
                    && !_pcbPlacement.HandlerRaised:
                    return false;
                case { } point when ActiveMotionGroup == MotionGroup.PcbSupply:
                    return _pcbSupply.IsMoveToTeachingPositionAllowed(point.Position);
                case { } point:
                    return (point.Position.Mode == TeachMode.ZOnly || IsHorizontalMoveAllowed)
                        && point.Position.HasPosition;
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
                    return _pcbPlacement.HandlerRaised;
                case MotionGroup.BoltFastening:
                    return _fasteningStation.IsHorizontalMoveAllowed;
                case MotionGroup.InspectionGantry:
                    return Inspection.IsRaised;
                default:
                    return false;
            }
        }
    }

    private void NotifyManualTeachingCommands()
    {
        OnPropertyChanged(nameof(HomeBlock));
        if (!State.ManualMode
            && (Inspection.IsLiveView || ToggleLiveViewCommand.IsRunning))
        {
            _ = RequestCameraStopAsync();
        }

        HomeCommand.NotifyCanExecuteChanged();
        JogCommand.NotifyCanExecuteChanged();
        StepCommand.NotifyCanExecuteChanged();
        MoveToHorizontalZCommand.NotifyCanExecuteChanged();
        foreach (var row in TeachingIoGroups.SelectMany(group => group.Outputs))
            row.ToggleOutputCommand.NotifyCanExecuteChanged();
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        MoveToPointCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ManualBlock));
        OnPropertyChanged(nameof(IsTeachingEditAllowed));
        OnPropertyChanged(nameof(MotionHint));
        SaveCommand.NotifyCanExecuteChanged();
        ReturnFromPickupCommand.NotifyCanExecuteChanged();
        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        GrabCommand.NotifyCanExecuteChanged();
        ApplyLightCommand.NotifyCanExecuteChanged();
        AddBoltPointCommand.NotifyCanExecuteChanged();
        RemoveBoltPointCommand.NotifyCanExecuteChanged();
    }
}
