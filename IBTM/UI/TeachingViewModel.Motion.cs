using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public enum TeachingMoveMode
{
    [Description("Jog · hold to move")]
    Jog,
    [Description("Step · move a set distance")]
    Step,
}

public enum TeachingDirection
{
    [Description("X−")]
    XMinus,
    [Description("X+")]
    XPlus,
    [Description("Y−")]
    YMinus,
    [Description("Y+")]
    YPlus,
    [Description("Z−")]
    ZMinus,
    [Description("Z+")]
    ZPlus,
}

public enum TeachingMotionHint
{
    [Description("")]
    None,
    [Description("This unit is disabled in Settings.")]
    UnitDisabled,
    [Description("Raise the NG pickup before moving XY.")]
    RaiseNgPickup,
    [Description("Z Jog/Step is available with the handler lowered. Raise the handler before X/Y, Move to Position or Move Z to Standby Height.")]
    RaisePlacementCylinders,
    [Description("Jog/Step adjust one axis at the current height. Raise both heads before moving to a teaching position.")]
    BoltAdjustment,
    [Description("Home this unit before jogging or moving to a teaching position.")]
    HomeRequired,
    [Description("Turn on this unit's axis servos before moving.")]
    ServoOff,
    [Description("Clear this unit's axis alarm or emergency signal before moving.")]
    AxisFault,
    [Description("Motion feedback is unavailable for this unit.")]
    MotionUnavailable,
    [Description("Record Carrier Pickup (S3) X/Y before moving to a carrier.")]
    NgPickupPositionRequired,
}

public partial class TeachingViewModel
{
    [ObservableProperty]
    public partial double JogSpeed { get; set; } = 10.0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StepCommand))]
    public partial double StepDistance { get; set; } = 0.1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualSpeedLabel))]
    public partial TeachingMoveMode MoveMode { get; set; }

    public TeachingMoveMode[] MoveModes { get; }

    public string ManualSpeedLabel => MoveMode == TeachingMoveMode.Step ? "Step speed" : "Jog speed";

    public MotionStatus Motion => State.GetMotionStatus(ActiveMotionGroup);

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
                && _settings.NgCarrierTransfer.CarrierPickupPosition is null)
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

    public IAsyncRelayCommand HomeCommand { get; }

    private async Task HomeAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            ViewCancellation);
        var group = ActiveMotionGroup;
        await Machine.HomeAsync(group, cancellation.Token);
    }

    private bool IsHomeAllowed => Motion.Feedback.Axes.All(axis => Machine.IsHomeAxisAllowed(ActiveMotionGroup, axis));

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

    public IRelayCommand JogStopCommand { get; }

    private void JogStop()
    {
        CancelTeaching();
    }

    private bool IsMoveToHorizontalZAllowed => IsJogAllowed(MotionAxis.Z)
        && (ActiveMotionGroup != MotionGroup.PcbPlacementHandler || _pcbPlacement.HandlerRaised);

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

    private bool IsStepAllowed(TeachingDirection direction)
    {
        if (!IsMoveDirectionAllowed(direction))
            return false;
        var position = Motion.Position;
        var (axis, sign) = Resolve(direction);
        var current = axis switch
        {
            MotionAxis.X => position.X,
            MotionAxis.Y => position.Y,
            MotionAxis.Z => position.Z,
            _ => null,
        };
        if (current is null)
            return false;
        return double.IsFinite(current.Value + sign * StepDistance);
    }

    private static (MotionAxis Axis, int Sign) Resolve(TeachingDirection direction)
    {
        switch (direction)
        {
            case TeachingDirection.XMinus:
                return (MotionAxis.X, -1);
            case TeachingDirection.XPlus:
                return (MotionAxis.X, 1);
            case TeachingDirection.YMinus:
                return (MotionAxis.Y, -1);
            case TeachingDirection.YPlus:
                return (MotionAxis.Y, 1);
            case TeachingDirection.ZMinus:
                return (MotionAxis.Z, -1);
            case TeachingDirection.ZPlus:
                return (MotionAxis.Z, 1);
            default:
                throw new ArgumentOutOfRangeException(nameof(direction));
        }
    }

    private bool IsMoveDirectionAllowed(TeachingDirection direction)
    {
        return IsJogAllowed(Resolve(direction).Axis);
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
}
