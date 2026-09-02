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
    public bool HasY => ActiveMotionGroup == MotionGroup.PcbSupply
        ? _supplyHandler.Feedback.HasY
        : _placementHandler.Feedback.HasY;
    public double HorizontalZ => ActiveMotionGroup == MotionGroup.PcbSupply
        ? _supplySettings.RotationZ
        : _placementSettings.BufferEntryZ;
    public TeachingTarget HorizontalZTarget =>
        ActiveMotionGroup == MotionGroup.PcbSupply
            ? TeachingTarget.SupplyRotationZ
            : TeachingTarget.PlacementBufferEntryZ;

    protected override MotionGroup CurrentMotionGroup =>
        ActiveMotionGroup;

    protected override (double X, double Y, double Z) CurrentPosition() =>
        ActiveMotionGroup == MotionGroup.PcbSupply
            ? _supplyHandler.Feedback.GetPosition()
            : _placementHandler.Feedback.GetPosition();

    protected override void JogCurrent(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken)
    {
        if (ActiveMotionGroup == MotionGroup.PcbSupply)
        {
            _supplyHandler.Jog(axis, velocity, cancellationToken);
        }
        else
        {
            _placementHandler.Jog(axis, velocity, cancellationToken);
        }
    }

    protected override Task MoveCurrentToHorizontalZAsync(
        CancellationToken cancellationToken) =>
        ActiveMotionGroup == MotionGroup.PcbSupply
            ? _supplyHandler.MoveToRotationZAsync(cancellationToken)
            : _placementHandler.MoveToHorizontalZAsync(cancellationToken);

    partial void OnSelectedPointChanged(
        TeachingPoint? oldValue,
        TeachingPoint? newValue)
    {
        CancelMotion();

        OnPropertyChanged(nameof(ActiveMotionGroup));
        OnPropertyChanged(nameof(HasY));
        OnPropertyChanged(nameof(HorizontalZ));
        OnPropertyChanged(nameof(HorizontalZTarget));
        NotifyManualTeachingCommands();
        RefreshPosition();
    }

    protected override bool CanJog(MotionAxis axis)
    {
        if (!CanUseCurrentHandler())
        {
            return false;
        }

        return axis switch
        {
            MotionAxis.X => ActiveMotionGroup == MotionGroup.PcbSupply
                ? _supplyHandler.IsAtRotationZ
                : _placementHandler.AtHorizontalZ,
            MotionAxis.Y =>
                HasY
                && (ActiveMotionGroup == MotionGroup.PcbSupply
                    ? _supplyHandler.IsAtRotationZ
                    : _placementHandler.AtHorizontalZ)
                && (ActiveMotionGroup != MotionGroup.PcbSupply
                    || !_buffer.SupplyInside),
            MotionAxis.Z =>
                ActiveMotionGroup != MotionGroup.PcbSupply
                || !_buffer.SupplyInside,
            _ => false,
        };
    }

    [RelayCommand(CanExecute = nameof(CanMoveToPoint))]
    private async Task MoveToPointAsync(CancellationToken cancellationToken)
    {
        var point = SelectedPoint!;
        await RunMotionAsync(async moveCancellation =>
        {
            if (point.MotionGroup == MotionGroup.PcbSupply)
            {
                await MoveSupplyPointAsync(point, moveCancellation);
            }
            else
            {
                await MovePlacementPointAsync(point, moveCancellation);
            }
        }, cancellationToken);
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
            case TeachMode.XYOnly:
                await _supplyHandler.MoveHorizontalAsync(
                    point.X,
                    point.Y,
                    cancellationToken);
                break;
            case TeachMode.XZOnly:
                await _supplyHandler.MoveHorizontalAsync(
                    point.X,
                    point.Y,
                    cancellationToken);
                await MoveSupplyZAsync(point, cancellationToken);
                break;
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
            ? _supplyHandler.LowerToHandoffAsync(cancellationToken)
            : _supplyHandler.MoveTeachingZAsync(
                point.Z!.Value,
                cancellationToken);

    private bool CanMoveToPoint() =>
        CanUseCurrentHandler()
        && SelectedPoint is { } point
        && (point.MotionGroup != MotionGroup.PcbSupply
            || CanMoveSupplyPoint(point));

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

    private async Task MovePlacementPointAsync(
        TeachingPoint point,
        CancellationToken cancellationToken)
    {
        switch (point.TeachMode)
        {
            case TeachMode.XOnly:
                await _placementHandler.MoveXAsync(point.X, cancellationToken);
                break;
            case TeachMode.YOnly:
                await _placementHandler.MoveYAsync(point.Y, cancellationToken);
                break;
            case TeachMode.ZOnly:
                await _placementHandler.MoveZAsync(
                    point.Z!.Value,
                    cancellationToken);
                break;
            case TeachMode.XYOnly:
                await _placementHandler.MoveToXYAsync(
                    point.X,
                    point.Y,
                    cancellationToken);
                break;
            case TeachMode.XZOnly:
                await _placementHandler.MoveToAsync(
                    point.X,
                    _placementHandler.Feedback.GetPosition().Y,
                    point.Z!.Value,
                    cancellationToken);
                break;
            case TeachMode.Full:
                await _placementHandler.MoveToAsync(
                    point.X,
                    point.Y,
                    point.Z!.Value,
                    cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private bool CanUseCurrentHandler() =>
        CanUseHandler(ActiveMotionGroup);

    private bool CanUseHandler(MotionGroup motionGroup) =>
        _state.ManualControlsEnabled
        && motionGroup switch
        {
            MotionGroup.PcbSupply =>
                _supplyEnabled
                && !_buffer.PlacementInside,
            MotionGroup.PcbPlacementHandler =>
                _placementEnabled
                && !_buffer.SupplyInside,
            _ => throw new ArgumentOutOfRangeException(nameof(motionGroup)),
        };

    protected override void NotifyManualTeachingCommands()
    {
        NotifyMotionCommands();
        TeachCurrentPositionCommand.NotifyCanExecuteChanged();
        MoveToPointCommand.NotifyCanExecuteChanged();
        ToggleActuatorCommand.NotifyCanExecuteChanged();
    }

}
