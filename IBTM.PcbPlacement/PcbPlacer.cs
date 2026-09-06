using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.PcbBuffer;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacer(
    BufferStage buffer,
    PcbPlacementHandler handler,
    PcbPlacementWork work)
{
    private HeatSinkSlot[]? _runTargets;

    public async Task RunAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        _runTargets = null;
        var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        buffer.StateChanged += OnStateChanged;
        handler.Changed += OnStateChanged;
        work.Changed += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!work.CarrierPresent || work.Completed)
                {
                    _runTargets = null;
                }
                else if (work.CarrierSeated)
                {
                    _runTargets ??= Enum.GetValues<HeatSinkSlot>()
                        .Where(work.HeatSinkPresent)
                        .ToArray();
                }

                var heatSink = NextHeatSink();
                switch (State(recipe, heatSink))
                {
                    case PcbPlacementState.RaisingHandler:
                        await handler.SetLiftDownAsync(
                            false,
                            cancellationToken);
                        break;

                    case PcbPlacementState.RaisingZ:
                        await handler.MoveToHorizontalZAsync(
                            cancellationToken);
                        break;

                    case PcbPlacementState.UnrotatingForBuffer:
                        await handler.SetRotatedAsync(
                            false,
                            cancellationToken);
                        break;

                    case PcbPlacementState.OpeningGripper:
                        await handler.SetIpmGripperAsync(
                            false,
                            cancellationToken);
                        break;

                    case PcbPlacementState.LoweringIpm:
                        await handler.SetIpmLiftDownAsync(
                            true,
                            cancellationToken);
                        break;

                    case PcbPlacementState.PressingPcb:
                        await handler.SetIpmGripperAsync(
                            false,
                            cancellationToken);
                        await handler.SetIpmGripperAsync(
                            true,
                            cancellationToken);
                        await handler.SetIpmLiftDownAsync(
                            true,
                            cancellationToken);
                        break;

                    case PcbPlacementState.MovingAboveBuffer:
                        await handler.MoveAboveBufferAsync(
                            cancellationToken);
                        break;

                    case PcbPlacementState.LoweringToBuffer:
                        await handler.LowerToBufferAsync(
                            cancellationToken);
                        break;

                    case PcbPlacementState.LoweringHandler:
                        await handler.SetLiftDownAsync(
                            true,
                            cancellationToken);
                        break;

                    case PcbPlacementState.WaitingForPcbDetection:
                        await handler.WaitForPcbAsync(
                            true,
                            cancellationToken);
                        break;

                    case PcbPlacementState.ApplyingVacuum:
                        await handler.SetVacuumAsync(
                            true,
                            cancellationToken);
                        break;

                    case PcbPlacementState.ClosingGripper:
                        await handler.SetIpmGripperAsync(
                            true,
                            cancellationToken);
                        break;

                    case PcbPlacementState.WaitingForSupplyExit:
                        await buffer.WaitForSupplyOutsideAsync(
                            cancellationToken);
                        break;

                    case PcbPlacementState.RaisingIpm:
                        await handler.SetIpmLiftDownAsync(
                            false,
                            cancellationToken);
                        break;

                    case PcbPlacementState.MovingToWaitPosition:
                        await handler.MoveAboveAsync(
                            recipe.HeatSink1PcbPlacementPosition,
                            cancellationToken);
                        break;

                    case PcbPlacementState.RotatingForPlacement:
                        await handler.SetRotatedAsync(
                            true,
                            cancellationToken);
                        break;

                    case PcbPlacementState.MovingAboveHeatSink:
                        await handler.MoveAboveAsync(
                            HeatSinkPosition(recipe, heatSink!.Value),
                            cancellationToken);
                        break;

                    case PcbPlacementState.LoweringToHeatSink:
                        await handler.LowerToAsync(
                            HeatSinkPosition(recipe, heatSink!.Value),
                            cancellationToken);
                        break;

                    case PcbPlacementState.ReleasingVacuum:
                        await handler.SetVacuumAsync(
                            false,
                            cancellationToken);
                        break;

                    case PcbPlacementState.RecordingPlacement:
                        work.Assembly(CurrentHeatSink(recipe)!.Value);
                        break;

                    case PcbPlacementState.CompletingCarrier:
                        work.Complete();
                        break;

                    default:
                        await stateChanged.WaitAsync(cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _runTargets = null;
            buffer.StateChanged -= OnStateChanged;
            handler.Changed -= OnStateChanged;
            work.Changed -= OnStateChanged;
        }
    }

    public PcbPlacementState State(PcbPlacementRecipe recipe) =>
        State(recipe, NextHeatSink());

    public HeatSinkSlot? TargetHeatSink => NextHeatSink();

    private PcbPlacementState State(
        PcbPlacementRecipe recipe,
        HeatSinkSlot? heatSink)
    {
        var pcb = handler.Pcb;
        var currentHeatSink = CurrentHeatSink(recipe);
        if (currentHeatSink is not null
            && work.CarrierSeated
            && !handler.VacuumDetected
            && (HeatSinkCompleted(currentHeatSink.Value)
                || (!work.Completed
                    && IsTarget(currentHeatSink.Value)
                    && pcb != PlacementPcbState.None)))
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

            if (work.CarrierSeated
                && heatSink is not null
                && handler.Rotation == PlacementRotationState.Rotated
                && handler.IsAtXY(HeatSinkPosition(recipe, heatSink.Value)))
            {
                return PlacementState(
                    HeatSinkPosition(recipe, heatSink.Value));
            }

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

            if (handler.Rotation != PlacementRotationState.Rotated)
            {
                return handler.IsAtXY(
                    recipe.HeatSink1PcbPlacementPosition)
                        ? PcbPlacementState.RotatingForPlacement
                        : PcbPlacementState.MovingToWaitPosition;
            }

            if (!work.CarrierSeated || work.Completed)
            {
                return PcbPlacementState.WaitingForCarrier;
            }

            return heatSink is null
                ? PcbPlacementState.CompletingCarrier
                : PlacementState(
                    HeatSinkPosition(recipe, heatSink.Value));
        }

        if (work.CarrierSeated
            && !work.Completed
            && heatSink is null)
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

        return buffer.CanPlacementEnter
            ? BufferPickupState()
            : PcbPlacementState.WaitingForBufferPcb;
    }

    private PcbPlacementState BufferPickupState()
    {
        var atBuffer = handler.AtBufferXY;
        var rotation = handler.Rotation;
        if (!atBuffer
            || rotation != PlacementRotationState.Unrotated)
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
            if (handler.Lift == PlacementCylinderState.Up
                && handler.AtHorizontalZ)
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

        if (ipm == PlacementCylinderState.Down)
        {
            return PcbPlacementState.RecordingPlacement;
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

        if (IsTarget(HeatSinkSlot.HeatSink1)
            && !HeatSinkCompleted(HeatSinkSlot.HeatSink1))
        {
            return HeatSinkSlot.HeatSink1;
        }

        return IsTarget(HeatSinkSlot.HeatSink2)
               && !HeatSinkCompleted(HeatSinkSlot.HeatSink2)
            ? HeatSinkSlot.HeatSink2
            : null;
    }

    private bool IsTarget(HeatSinkSlot heatSink) =>
        _runTargets?.Contains(heatSink) ?? work.HeatSinkPresent(heatSink);

    private bool HeatSinkCompleted(HeatSinkSlot heatSink) =>
        work.Assemblies.Any(assembly => assembly.HeatSink == heatSink);

    private HeatSinkSlot? CurrentHeatSink(PcbPlacementRecipe recipe)
    {
        if (handler.IsAtXY(recipe.HeatSink1PcbPlacementPosition))
        {
            return HeatSinkSlot.HeatSink1;
        }

        return handler.IsAtXY(recipe.HeatSink2PcbPlacementPosition)
            ? HeatSinkSlot.HeatSink2
            : null;
    }

    private static AxisPosition HeatSinkPosition(
        PcbPlacementRecipe recipe,
        HeatSinkSlot heatSink) =>
        heatSink == HeatSinkSlot.HeatSink1
            ? recipe.HeatSink1PcbPlacementPosition
            : recipe.HeatSink2PcbPlacementPosition;
}
