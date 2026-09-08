using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;

namespace IBTM.UI;

public partial class SupplyTeachingViewModel
{
    public MotionGroup ActiveMotionGroup =>
        SelectedPoint?.MotionGroup ?? MotionGroup.PcbSupply;
    public override TeachingMotionHint MotionHint
    {
        get
        {
            var block = HandlerBlock(ActiveMotionGroup);
            if (block != TeachingMotionHint.None) return block;
            if (ActiveMotionGroup == MotionGroup.PcbSupply && _buffer.SupplyInside)
                return TeachingMotionHint.SupplyInBufferRestricted;
            if (ActiveMotionGroup == MotionGroup.PcbPlacementHandler
                && !_placementHandler.CanMoveHorizontal)
                return TeachingMotionHint.RaisePlacementCylinders;
            return _state.ManualControlsEnabled && !CanJog(MotionAxis.X)
                ? TeachingMotionHint.SafeZRequired
                : TeachingMotionHint.None;
        }
    }
    public double HorizontalZ => ActiveMotionGroup == MotionGroup.PcbSupply
        ? _supplySettings.RotationZ
        : _placementSettings.BufferEntryZ;

    protected override MotionGroup CurrentMotionGroup =>
        ActiveMotionGroup;

    protected override Task JogCurrentAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken) =>
        ActiveMotionGroup == MotionGroup.PcbSupply
            ? _supplyHandler.JogAsync(axis, velocity, cancellationToken)
            : _placementHandler.JogAsync(axis, velocity, cancellationToken);

    protected override Task MoveCurrentToHorizontalZAsync(
        CancellationToken cancellationToken) =>
        ActiveMotionGroup == MotionGroup.PcbSupply
            ? _supplyHandler.MoveToRotationZAsync(cancellationToken)
            : _placementHandler.MoveToHorizontalZAsync(cancellationToken);

    protected override Task MoveCurrentAxisAsync(
        MotionAxis axis, double position, CancellationToken cancellationToken) =>
        (ActiveMotionGroup, axis) switch
        {
            (MotionGroup.PcbSupply, MotionAxis.X) => _supplyHandler.MoveXAsync(position, cancellationToken),
            (MotionGroup.PcbSupply, MotionAxis.Y) => _supplyHandler.MoveYAsync(position, cancellationToken),
            (MotionGroup.PcbSupply, MotionAxis.Z) => _supplyHandler.MoveTeachingZAsync(position, cancellationToken),
            (MotionGroup.PcbPlacementHandler, MotionAxis.X) => _placementHandler.MoveXAsync(position, cancellationToken),
            (MotionGroup.PcbPlacementHandler, MotionAxis.Y) => _placementHandler.MoveYAsync(position, cancellationToken),
            (MotionGroup.PcbPlacementHandler, MotionAxis.Z) => _placementHandler.MoveZAsync(position, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };

    partial void OnSelectedPointChanged(
        TeachingPoint? oldValue,
        TeachingPoint? newValue)
    {
        CancelMotion();
        NotifyPointSelectionCommands();

        OnPropertyChanged(nameof(ActiveMotionGroup));
        OnPropertyChanged(nameof(HasY));
        OnPropertyChanged(nameof(HasZ));
        OnPropertyChanged(nameof(HorizontalZ));
        OnPropertyChanged(nameof(SaveBehavior));
        NotifyManualTeachingCommands();
        OnPropertyChanged(nameof(Motion));
    }

    protected override bool CanJog(MotionAxis axis)
    {
        if (!CanUseCurrentHandler())
        {
            return false;
        }

        return axis switch
        {
            MotionAxis.Y when !HasY
                || ActiveMotionGroup == MotionGroup.PcbSupply && _buffer.SupplyInside => false,
            MotionAxis.X or MotionAxis.Y => ActiveMotionGroup == MotionGroup.PcbSupply
                ? _supplyHandler.IsAtRotationZ
                : _placementHandler.CanMoveHorizontal
                  && _placementHandler.AtHorizontalZ,
            MotionAxis.Z =>
                ActiveMotionGroup != MotionGroup.PcbSupply
                || !_buffer.SupplyInside,
            _ => false,
        };
    }

    [RelayCommand(CanExecute = nameof(CanMoveToPoint))]
    private Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint!;
        return RunMotionAsync(
            moveCancellation =>
                point.MotionGroup == MotionGroup.PcbSupply
                    ? MoveSupplyPointAsync(point, moveCancellation)
                    : MovePlacementPointAsync(point, moveCancellation),
            cancellationToken);
    }

    private async Task MoveSupplyPointAsync(
        TeachingPoint point,
        CancellationToken cancellationToken)
    {
        switch (point.TeachMode)
        {
            case TeachMode.XOnly:
                await _supplyHandler.MoveXAsync(point.X, cancellationToken);
                break;
            case TeachMode.YOnly:
                await _supplyHandler.MoveYAsync(point.Y, cancellationToken);
                break;
            case TeachMode.ZOnly:
                await _supplyHandler.MoveTeachingZAsync(
                    point.Z!.Value,
                    cancellationToken);
                break;
            case TeachMode.XZOnly:
            case TeachMode.Full:
                await _supplyHandler.MoveHorizontalAsync(
                    point.X,
                    point.Y,
                    cancellationToken);
                await MoveSupplyZAsync(point, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private Task MoveSupplyZAsync(
        TeachingPoint point,
        CancellationToken cancellationToken) =>
        point.Target == TeachingTarget.SupplyBufferHandoff
            ? _supplyHandler.LowerToHandoffAsync(cancellationToken, point.Z!.Value)
            : _supplyHandler.MoveTeachingZAsync(
                point.Z!.Value,
                cancellationToken);

    private bool CanMoveToPoint() =>
        CanUseCurrentHandler()
        && SelectedPoint is { } point
        && (point.MotionGroup != MotionGroup.PcbSupply
            ? point.TeachMode == TeachMode.ZOnly
              || _placementHandler.CanMoveHorizontal
            : CanMoveSupplyPoint(point));

    private bool CanMoveSupplyPoint(TeachingPoint point) =>
        (!_buffer.SupplyInside
         || point.TeachMode == TeachMode.XOnly
         && _supplyHandler.IsAtRotationZ)
        && point.Target switch
        {
            TeachingTarget.SupplyBufferHandoff =>
                _supplyHandler.Rotation == PcbSupplyRotationState.Rotated,
            TeachingTarget.SupplyPcb1Pick
                or TeachingTarget.SupplyPcb2Pick =>
                _supplyHandler.Rotation == PcbSupplyRotationState.Unrotated,
            _ => true,
        };

    private Task MovePlacementPointAsync(
        TeachingPoint point,
        CancellationToken cancellationToken) =>
        point.TeachMode switch
        {
            TeachMode.ZOnly =>
                _placementHandler.MoveZAsync(
                    point.Z!.Value,
                    cancellationToken),
            TeachMode.XYOnly =>
                _placementHandler.MoveToXYAsync(
                    point.X,
                    point.Y,
                    cancellationToken),
            TeachMode.Full =>
                _placementHandler.MoveToAsync(
                    point.X,
                    point.Y,
                    point.Z!.Value,
                    cancellationToken),
            _ => throw new ArgumentOutOfRangeException(),
        };

    protected override bool CanUseCurrentHandler() =>
        CanUseHandler(ActiveMotionGroup);

    private bool CanUseHandler(MotionGroup motionGroup) =>
        _state.ManualControlsEnabled
        && HandlerBlock(motionGroup) == TeachingMotionHint.None;

    private TeachingMotionHint HandlerBlock(MotionGroup motionGroup) =>
        motionGroup switch
        {
            MotionGroup.PcbSupply when !SupplyEnabled => TeachingMotionHint.UnitDisabled,
            MotionGroup.PcbPlacementHandler when !PlacementEnabled => TeachingMotionHint.UnitDisabled,
            MotionGroup.PcbSupply when _buffer.PlacementInside => TeachingMotionHint.PlacementInBuffer,
            MotionGroup.PcbPlacementHandler when _buffer.SupplyInside => TeachingMotionHint.SupplyInBuffer,
            MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler => TeachingMotionHint.None,
            _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
        };

    protected override void NotifyManualTeachingCommands()
    {
        NotifyMotionCommands();
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        SaveBufferSetupCommand.NotifyCanExecuteChanged();
        MoveToPointCommand.NotifyCanExecuteChanged();
    }

}
