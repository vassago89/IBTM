using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.PcbBuffer;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacer(BufferStage buffer, PcbPlacementHandler handler, PcbPlacementWork work) : AutoUnit
{
    private HeatSinkSlot[]? _runTargets;
    // Down before release and Down after pressing have identical IO feedback.
    // Keep only the pending press target across Stop, never a cached cylinder state.
    private HeatSinkSlot? _pressingHeatSink;

    public override event Action? Changed
    {
        add
        {
            buffer.StateChanged += value;
            handler.Changed += value;
            work.Changed += value;
        }

        remove
        {
            buffer.StateChanged -= value;
            handler.Changed -= value;
            work.Changed -= value;
        }
    }

    public async Task RunAsync(PcbPlacementRecipe recipe, CancellationToken cancellationToken = default)
    {
        _runTargets = null;
        try
        {
            await RunLoopAsync(token => ExecuteAsync(recipe, token), cancellationToken);
        }
        finally
        {
            _runTargets = null;
        }
    }

    private Task ExecuteAsync(PcbPlacementRecipe recipe, CancellationToken cancellationToken)
    {
        if (!work.CarrierPresent || work.Completed)
        {
            _runTargets = null;
        }
        else if (work.CarrierSeated)
        {
            _runTargets ??= Enum.GetValues<HeatSinkSlot>().Where(work.HeatSinkPresent).ToArray();
        }

        return PlaceStepAsync(recipe, NextHeatSink(), cancellationToken) ?? WaitForChangeAsync(
            cancellationToken);
    }

    public bool PlacementComplete(HeatSinkSlot heatSink)
    {
        return HeatSinkCompleted(heatSink)
            && handler.Pcb == PlacementPcbState.None
            && handler.Lift == PlacementCylinderState.Up
            && handler.AtHorizontalZ;
    }

    // Execute one production action for the requested heat sink; null means waiting.
    public Task? PlaceStepAsync(
        PcbPlacementRecipe recipe,
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken)
    {
        switch (State(recipe, heatSink))
        {
            case PcbPlacementState.RaisingHandler:
                return handler.SetLiftDownAsync(false, cancellationToken);
            case PcbPlacementState.RaisingZ:
                return handler.MoveToHorizontalZAsync(cancellationToken);
            case PcbPlacementState.UnrotatingForBuffer:
                return handler.SetRotatedAsync(false, cancellationToken);
            case PcbPlacementState.OpeningGripper:
                return handler.SetIpmGripperAsync(false, cancellationToken);
            case PcbPlacementState.LoweringIpm:
                return handler.SetIpmLiftDownAsync(true, cancellationToken);
            case PcbPlacementState.PressingPcb:
                return PressPcbAsync(CurrentHeatSink(recipe)!.Value, cancellationToken);
            case PcbPlacementState.MovingAboveBuffer:
                return handler.MoveAboveBufferAsync(cancellationToken);
            case PcbPlacementState.LoweringToBuffer:
                return handler.LowerToBufferAsync(cancellationToken);
            case PcbPlacementState.LoweringHandler:
                return handler.SetLiftDownAsync(true, cancellationToken);
            case PcbPlacementState.WaitingForPcbDetection:
                return handler.WaitForPcbAsync(cancellationToken);
            case PcbPlacementState.ApplyingVacuum:
                return handler.SetVacuumAsync(true, cancellationToken);
            case PcbPlacementState.ClosingGripper:
                return handler.SetIpmGripperAsync(true, cancellationToken);
            case PcbPlacementState.WaitingForSupplyExit:
                return buffer.WaitForSupplyOutsideAsync(cancellationToken);
            case PcbPlacementState.RaisingIpm:
                return handler.SetIpmLiftDownAsync(false, cancellationToken);
            case PcbPlacementState.MovingToWaitPosition:
                return handler.MoveAboveAsync(recipe.HeatSink1PcbPlacementPosition, cancellationToken);
            case PcbPlacementState.RotatingForPlacement:
                return handler.SetRotatedAsync(true, cancellationToken);
            case PcbPlacementState.MovingAboveHeatSink:
                return handler.MoveAboveAsync(
                    HeatSinkPosition(recipe, heatSink!.Value),
                    cancellationToken);
            case PcbPlacementState.LoweringToHeatSink:
                return handler.LowerToAsync(HeatSinkPosition(recipe, heatSink!.Value), cancellationToken);
            case PcbPlacementState.ReleasingVacuum:
                _pressingHeatSink = null;
                return handler.SetVacuumAsync(false, cancellationToken);
            case PcbPlacementState.RecordingPlacement:
                work.Assembly(CurrentHeatSink(recipe)!.Value);
                _pressingHeatSink = null;
                break;
            case PcbPlacementState.CompletingCarrier:
                work.Complete();
                break;
            default:
                return null;
        }

        return Task.CompletedTask;
    }

    private async Task PressPcbAsync(HeatSinkSlot heatSink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pressingHeatSink = heatSink;
        await handler.SetIpmGripperAsync(true, cancellationToken);
        await handler.SetIpmLiftDownAsync(true, cancellationToken);
    }

    public PcbPlacementState State(PcbPlacementRecipe recipe)
    {
        return State(recipe, NextHeatSink());
    }

    public HeatSinkSlot? TargetHeatSink
    {
        get
        {
            return NextHeatSink();
        }
    }

    public PcbPlacementState State(PcbPlacementRecipe recipe, HeatSinkSlot? heatSink)
    {
        var pcb = handler.Pcb;
        var currentHeatSink = CurrentHeatSink(recipe);
        if (currentHeatSink is not null
            && work.CarrierSeated
            && !handler.VacuumDetected
            && (HeatSinkCompleted(currentHeatSink.Value)
                || (!work.Completed
                    && IsTarget(currentHeatSink.Value)
                    && pcb != PlacementPcbState.None
                    && handler.Rotation == PlacementRotationState.Rotated
                    && handler.IsAtZ(HeatSinkPosition(recipe, currentHeatSink.Value))
                    && handler.Lift == PlacementCylinderState.Down)))
        {
            var state = FinishPlacementState(currentHeatSink.Value);
            if (state is not null)
            {
                return state.Value;
            }
        }

        if (pcb == PlacementPcbState.Secured)
        {
            if (buffer.PlacementInside && buffer.SupplyInside)
            {
                return PcbPlacementState.WaitingForSupplyExit;
            }

            if (handler.IpmLift != PlacementCylinderState.Down)
            {
                return PcbPlacementState.LoweringIpm;
            }

            if (work.CarrierSeated
                && heatSink is not null
                && handler.Rotation == PlacementRotationState.Rotated
                && handler.IsAtXY(HeatSinkPosition(recipe, heatSink.Value)))
            {
                return PlacementState(HeatSinkPosition(recipe, heatSink.Value));
            }

            if (handler.Lift != PlacementCylinderState.Up)
            {
                return PcbPlacementState.RaisingHandler;
            }

            if (!handler.AtHorizontalZ)
            {
                return PcbPlacementState.RaisingZ;
            }

            if (handler.Rotation != PlacementRotationState.Rotated)
            {
                return handler.IsAtXY(recipe.HeatSink1PcbPlacementPosition)
                    ? PcbPlacementState.RotatingForPlacement
                    : PcbPlacementState.MovingToWaitPosition;
            }

            if (!work.CarrierSeated || work.Completed)
            {
                return PcbPlacementState.WaitingForCarrier;
            }

            return heatSink is null
                ? PcbPlacementState.CompletingCarrier
                : PlacementState(HeatSinkPosition(recipe, heatSink.Value));
        }

        if (work.CarrierSeated && !work.Completed && heatSink is null)
        {
            if (handler.IpmLift != PlacementCylinderState.Up)
            {
                return PcbPlacementState.RaisingIpm;
            }

            if (handler.Lift != PlacementCylinderState.Up)
            {
                return PcbPlacementState.RaisingHandler;
            }

            if (!handler.AtHorizontalZ)
            {
                return PcbPlacementState.RaisingZ;
            }

            return PcbPlacementState.CompletingCarrier;
        }

        return buffer.CanPlacementEnter ? BufferPickupState() : PcbPlacementState.WaitingForBufferPcb;
    }

    private PcbPlacementState BufferPickupState()
    {
        var atBuffer = handler.AtBufferXY;
        var rotation = handler.Rotation;
        if (!atBuffer || rotation != PlacementRotationState.Unrotated)
        {
            if (handler.Lift != PlacementCylinderState.Up)
            {
                return PcbPlacementState.RaisingHandler;
            }

            if (!handler.AtHorizontalZ)
            {
                return PcbPlacementState.RaisingZ;
            }
        }

        if (rotation != PlacementRotationState.Unrotated)
        {
            return PcbPlacementState.UnrotatingForBuffer;
        }

        if (handler.IpmGripper != PlacementGripperState.Open)
        {
            return PcbPlacementState.OpeningGripper;
        }

        if (handler.IpmLift != PlacementCylinderState.Down)
        {
            return PcbPlacementState.LoweringIpm;
        }

        if (!atBuffer)
        {
            return PcbPlacementState.MovingAboveBuffer;
        }

        if (!handler.AtBufferZ)
        {
            return PcbPlacementState.LoweringToBuffer;
        }

        if (handler.Lift != PlacementCylinderState.Down)
        {
            return PcbPlacementState.LoweringHandler;
        }

        if (handler.Pcb == PlacementPcbState.None)
        {
            return PcbPlacementState.WaitingForPcbDetection;
        }

        if (!handler.VacuumDetected)
        {
            return PcbPlacementState.ApplyingVacuum;
        }

        return PcbPlacementState.ClosingGripper;
    }

    private PcbPlacementState PlacementState(AxisPosition position)
    {
        if (!handler.IsAtXY(position))
        {
            return PcbPlacementState.MovingAboveHeatSink;
        }

        if (!handler.IsAtZ(position))
        {
            return PcbPlacementState.LoweringToHeatSink;
        }

        if (handler.Lift != PlacementCylinderState.Down)
        {
            return PcbPlacementState.LoweringHandler;
        }

        if (handler.VacuumDetected)
        {
            return PcbPlacementState.ReleasingVacuum;
        }

        if (handler.IpmGripper != PlacementGripperState.Open)
        {
            return PcbPlacementState.OpeningGripper;
        }

        return PcbPlacementState.WaitingForBufferPcb;
    }

    private PcbPlacementState? FinishPlacementState(HeatSinkSlot heatSink)
    {
        var ipm = handler.IpmLift;
        if (HeatSinkCompleted(heatSink))
        {
            if (handler.Lift == PlacementCylinderState.Up && handler.AtHorizontalZ)
            {
                return null;
            }

            if (ipm != PlacementCylinderState.Up)
            {
                return PcbPlacementState.RaisingIpm;
            }

            if (handler.Lift != PlacementCylinderState.Up)
            {
                return PcbPlacementState.RaisingHandler;
            }

            return PcbPlacementState.RaisingZ;
        }

        if (_pressingHeatSink == heatSink)
        {
            return ipm == PlacementCylinderState.Down
                && handler.IpmGripper == PlacementGripperState.Closed
                ? PcbPlacementState.RecordingPlacement
                : PcbPlacementState.PressingPcb;
        }

        if (handler.IpmGripper != PlacementGripperState.Open)
        {
            return PcbPlacementState.OpeningGripper;
        }

        if (ipm != PlacementCylinderState.Up)
        {
            return PcbPlacementState.RaisingIpm;
        }

        return PcbPlacementState.PressingPcb;
    }

    private HeatSinkSlot? NextHeatSink()
    {
        if (work.Completed)
        {
            return null;
        }

        if (IsTarget(HeatSinkSlot.HeatSink1) && !HeatSinkCompleted(HeatSinkSlot.HeatSink1))
        {
            return HeatSinkSlot.HeatSink1;
        }

        return IsTarget(HeatSinkSlot.HeatSink2) && !HeatSinkCompleted(HeatSinkSlot.HeatSink2)
            ? HeatSinkSlot.HeatSink2
            : null;
    }

    private bool IsTarget(HeatSinkSlot heatSink)
    {
        return _runTargets?.Contains(heatSink) ?? work.HeatSinkPresent(heatSink);
    }

    private bool HeatSinkCompleted(HeatSinkSlot heatSink)
    {
        return work.Assemblies.Any(assembly => assembly.HeatSink == heatSink);
    }

    private HeatSinkSlot? CurrentHeatSink(PcbPlacementRecipe recipe)
    {
        if (handler.IsAtXY(recipe.HeatSink1PcbPlacementPosition))
        {
            return HeatSinkSlot.HeatSink1;
        }

        return handler.IsAtXY(recipe.HeatSink2PcbPlacementPosition) ? HeatSinkSlot.HeatSink2 : null;
    }

    private static AxisPosition HeatSinkPosition(PcbPlacementRecipe recipe, HeatSinkSlot heatSink)
    {
        return heatSink == HeatSinkSlot.HeatSink1
            ? recipe.HeatSink1PcbPlacementPosition
            : recipe.HeatSink2PcbPlacementPosition;
    }
}
