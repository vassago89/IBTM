using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class SupplyTeachingViewModel
{
    public override MotionGroup ActiveMotionGroup
    {
        get
        {
            return SelectedPoint?.Position.MotionGroup ?? MotionGroup.PcbSupply;
        }
    }

    public override TeachingMotionHint MotionHint
    {
        get
        {
            if (!_state.Display.Available)
                return TeachingMotionHint.None;
            var block = HandlerBlock(ActiveMotionGroup);
            if (block != TeachingMotionHint.None)
                return block;
            if (ActiveMotionGroup == MotionGroup.PcbSupply
                && _state.Display.SupplyInBufferArea)
                return TeachingMotionHint.SupplyInBufferRestricted;
            if (ActiveMotionGroup == MotionGroup.PcbPlacementHandler
                && !_placementHandler.CanMoveHorizontal)
                return TeachingMotionHint.RaisePlacementCylinders;
            return CanEditTeaching && !CanJog(MotionAxis.X)
                ? TeachingMotionHint.SafeZRequired
                : TeachingMotionHint.None;
        }
    }

    public double HorizontalZ
    {
        get
        {
            return ActiveMotionGroup == MotionGroup.PcbSupply
                ? _supplySettings.RotationZ
                : _placementSettings.BufferEntryZ;
        }
    }

    protected override Task JogAsync(TeachingDirection direction, CancellationToken cancellationToken)
    {
        var (axis, sign) = Resolve(direction);
        return Machine.RunManualMotionAsync(
            ActiveMotionGroup,
            token => ActiveMotionGroup == MotionGroup.PcbSupply
                ? _supplyHandler.JogAsync(axis, sign * JogSpeed, token)
                : _placementHandler.JogAsync(axis, sign * JogSpeed, token),
            cancellationToken,
            ViewCancellation);
    }

    protected override Task MoveToHorizontalZAsync(CancellationToken cancellationToken)
    {
        return Machine.RunManualMotionAsync(
            ActiveMotionGroup,
            token => ActiveMotionGroup == MotionGroup.PcbSupply
                ? _supplyHandler.MoveToRotationZAsync(token)
                : _placementHandler.MoveToHorizontalZAsync(token),
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
                return (ActiveMotionGroup, axis) switch
                {
                    (MotionGroup.PcbSupply, MotionAxis.X)
                        => _supplyHandler.MoveXAsync(target, token),
                    (MotionGroup.PcbSupply, MotionAxis.Y)
                        => _supplyHandler.MoveYAsync(target, token),
                    (MotionGroup.PcbSupply, MotionAxis.Z)
                        => _supplyHandler.MoveTeachingZAsync(target, token),
                    (MotionGroup.PcbPlacementHandler, _)
                        => _placementHandler.MoveAxisAsync(axis, target, token),
                    _ => throw new ArgumentOutOfRangeException(nameof(axis)),
                };
            },
            cancellationToken,
            ViewCancellation);
    }

    partial void OnSelectedPointChanged(TeachingPoint? oldValue, TeachingPoint? newValue)
    {
        CancelTeaching();
        NotifyPointSelectionCommands();

        OnPropertyChanged(nameof(ActiveMotionGroup));
        OnPropertyChanged(nameof(HorizontalZ));
        OnPropertyChanged(nameof(SaveBehavior));
        NotifyManualTeachingCommands();
        OnPropertyChanged(nameof(Motion));
    }

    protected override bool CanJog(MotionAxis axis)
    {
        return Machine.CanUseManualMotion(ActiveMotionGroup, live: false)
            && (ActiveMotionGroup == MotionGroup.PcbSupply
                ? _supplyHandler.CanJog(axis, live: false)
                : _placementHandler.CanJog(axis, live: false));
    }

    protected override Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint!;
        return Machine.RunManualMotionAsync(
            ActiveMotionGroup,
            token => point.Position.MotionGroup == MotionGroup.PcbSupply
                ? _supplyHandler.MoveToTeachingPositionAsync(point.Position, point.Read(), token)
                : _placementHandler.MoveToTeachingPositionAsync(point.Position, point.Read(), token),
            cancellationToken,
            ViewCancellation);
    }

    protected override bool CanMoveToPoint()
    {
        return Machine.CanUseManualMotion(ActiveMotionGroup, live: false)
            && SelectedPoint is { } point
            && (point.Position.MotionGroup != MotionGroup.PcbSupply
                ? point.Position.Mode == TeachMode.ZOnly || _placementHandler.CanMoveHorizontal
                : _supplyHandler.CanMoveToTeachingPosition(point.Position, live: false));
    }

    private TeachingMotionHint HandlerBlock(MotionGroup motionGroup)
    {
        return motionGroup switch
        {
            MotionGroup.PcbSupply when !SupplyEnabled => TeachingMotionHint.UnitDisabled,
            MotionGroup.PcbPlacementHandler when !PlacementEnabled => TeachingMotionHint.UnitDisabled,
            MotionGroup.PcbSupply when _state.Display.PlacementInBufferArea
                => TeachingMotionHint.PlacementInBuffer,
            MotionGroup.PcbPlacementHandler when _state.Display.SupplyInBufferArea
                => TeachingMotionHint.SupplyInBuffer,
            MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler => TeachingMotionHint.None,
            _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
        };
    }

    protected override void NotifyManualTeachingCommands()
    {
        NotifyMotionCommands();
        SaveBufferSetupCommand.NotifyCanExecuteChanged();
    }

}
