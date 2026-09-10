using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class SupplyTeachingViewModel
{
    public MotionGroup ActiveMotionGroup
    {
        get
        {
            return SelectedPoint?.MotionGroup ?? MotionGroup.PcbSupply;
        }
    }

    public override TeachingMotionHint MotionHint
    {
        get
        {
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

    protected override MotionGroup CurrentMotionGroup
    {
        get
        {
            return ActiveMotionGroup;
        }
    }

    protected override Task JogCurrentAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken)
    {
        return ActiveMotionGroup == MotionGroup.PcbSupply
            ? _supplyHandler.JogAsync(axis, velocity, cancellationToken)
            : _placementHandler.JogAsync(axis, velocity, cancellationToken);
    }

    protected override Task MoveCurrentToHorizontalZAsync(CancellationToken cancellationToken)
    {
        return ActiveMotionGroup == MotionGroup.PcbSupply
            ? _supplyHandler.MoveToRotationZAsync(cancellationToken)
            : _placementHandler.MoveToHorizontalZAsync(cancellationToken);
    }

    protected override Task MoveCurrentAxisAsync(
        MotionAxis axis,
        double position,
        CancellationToken cancellationToken)
    {
        return (ActiveMotionGroup, axis) switch
        {
            (MotionGroup.PcbSupply, MotionAxis.X)

                => _supplyHandler.MoveXAsync(position, cancellationToken),
            (MotionGroup.PcbSupply, MotionAxis.Y)

                => _supplyHandler.MoveYAsync(position, cancellationToken),
            (MotionGroup.PcbSupply, MotionAxis.Z)

                => _supplyHandler.MoveTeachingZAsync(position, cancellationToken),
            (MotionGroup.PcbPlacementHandler, MotionAxis.X)

                => _placementHandler.MoveXAsync(position, cancellationToken),
            (MotionGroup.PcbPlacementHandler, MotionAxis.Y)

                => _placementHandler.MoveYAsync(position, cancellationToken),
            (MotionGroup.PcbPlacementHandler, MotionAxis.Z)

                => _placementHandler.MoveZAsync(position, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
    }

    partial void OnSelectedPointChanged(TeachingPoint? oldValue, TeachingPoint? newValue)
    {
        CancelTeaching();
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
        return Machine.CanUseManualMotion(CurrentMotionGroup, live: false)
            && (ActiveMotionGroup == MotionGroup.PcbSupply
                ? _supplyHandler.CanJog(axis, live: false)
                : _placementHandler.CanJog(axis, live: false));
    }

    protected override Task MovePointAsync(TeachingPoint point, CancellationToken cancellationToken)
    {
        return point.MotionGroup == MotionGroup.PcbSupply
            ? _supplyHandler.MoveToTeachingPositionAsync(point.Position, point.Read(), cancellationToken)
            : _placementHandler.MoveToTeachingPositionAsync(
                point.Position,
                point.Read(),
                cancellationToken);
    }

    protected override bool CanMoveToPoint()
    {
        return Machine.CanUseManualMotion(CurrentMotionGroup, live: false)
            && SelectedPoint is { } point
            && (point.MotionGroup != MotionGroup.PcbSupply
                ? point.TeachMode == TeachMode.ZOnly || _placementHandler.CanMoveHorizontal
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
