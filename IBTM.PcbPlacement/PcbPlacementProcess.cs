using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.PcbBuffer;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementProcess(
    BufferStage buffer,
    PcbPlacementHandler handler,
    PcbPlacementWork work)
{
    public async Task RunAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        using var stateChanged = new AsyncAutoResetEvent();
        void OnStateChanged() => stateChanged.Set();

        handler.Changed += OnStateChanged;
        buffer.StateChanged += OnStateChanged;
        work.Changed += OnStateChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var heatSink = NextHeatSink();
                switch (State(recipe, heatSink))
                {
                    case PcbPlacementState.RaisingHandler:
                        await handler.SetHandlerDownAsync(
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
                        await handler.SetIpmDownAsync(
                            true,
                            cancellationToken);
                        break;

                    case PcbPlacementState.PressingPcb:
                        await handler.SetIpmGripperAsync(
                            true,
                            cancellationToken);
                        await handler.SetIpmDownAsync(
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
                        await handler.SetHandlerDownAsync(
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
                        await handler.SetIpmDownAsync(
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
                        work.Assembly(heatSink!.Value);
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
            handler.Changed -= OnStateChanged;
            buffer.StateChanged -= OnStateChanged;
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
        var currentHeatSink = CurrentHeatSink(recipe);
        if (currentHeatSink is not null
            && work.Ready
            && work.HeatSinkPresent(currentHeatSink.Value)
            && !handler.VacuumDetected
            && handler.Pcb != PlacementPcbState.None)
        {
            var state = FinishPlacementState(currentHeatSink.Value);
            if (state is not null)
            {
                return state.Value;
            }
        }

        if (handler.Pcb == PlacementPcbState.Secured)
        {
            if (buffer.PlacementInside && buffer.SupplyInside)
            {
                return PcbPlacementState.WaitingForSupplyExit;
            }

            if (work.Ready
                && heatSink is not null
                && handler.Rotation == PlacementRotationState.Rotated
                && handler.IsAtXY(HeatSinkPosition(recipe, heatSink.Value)))
            {
                return PlacementState(
                    HeatSinkPosition(recipe, heatSink.Value));
            }

            if (handler.Ipm != PlacementCylinderState.Up)
            {
                return PcbPlacementState.RaisingIpm;
            }

            if (handler.Handler != PlacementCylinderState.Up)
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

            if (!work.Ready)
            {
                return PcbPlacementState.WaitingForCarrier;
            }

            return heatSink is null
                ? PcbPlacementState.CompletingCarrier
                : PlacementState(
                    HeatSinkPosition(recipe, heatSink.Value));
        }

        if (work.Ready && !work.Completed && heatSink is null)
        {
            return PcbPlacementState.CompletingCarrier;
        }

        return buffer.CanPlacementEnter
            ? BufferPickupState()
            : PcbPlacementState.WaitingForPcb;
    }

    private PcbPlacementState BufferPickupState()
    {
        var needsClearance = !handler.AtBufferXY
            || handler.Rotation != PlacementRotationState.Unrotated;
        if (needsClearance)
        {
            if (handler.Handler != PlacementCylinderState.Up)
            {
                return PcbPlacementState.RaisingHandler;
            }

            if (!handler.AtHorizontalZ)
            {
                return PcbPlacementState.RaisingZ;
            }
        }

        if (handler.Rotation != PlacementRotationState.Unrotated)
        {
            return PcbPlacementState.UnrotatingForBuffer;
        }

        if (!handler.AtBufferXY)
        {
            return PcbPlacementState.MovingAboveBuffer;
        }

        if (handler.Gripper != PlacementGripperState.Open)
        {
            return PcbPlacementState.OpeningGripper;
        }

        if (handler.Ipm != PlacementCylinderState.Down)
        {
            return PcbPlacementState.LoweringIpm;
        }

        if (!handler.AtBufferZ)
        {
            return PcbPlacementState.LoweringToBuffer;
        }

        if (handler.Handler != PlacementCylinderState.Down)
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

    private PcbPlacementState PlacementState(AxisPos position)
    {
        if (!handler.IsAtXY(position))
        {
            return PcbPlacementState.MovingAboveHeatSink;
        }

        if (!handler.IsAtZ(position))
        {
            return PcbPlacementState.LoweringToHeatSink;
        }

        if (handler.Handler != PlacementCylinderState.Down)
        {
            return PcbPlacementState.LoweringHandler;
        }

        if (handler.VacuumDetected)
        {
            return PcbPlacementState.ReleasingVacuum;
        }

        if (handler.Gripper != PlacementGripperState.Open)
        {
            return PcbPlacementState.OpeningGripper;
        }

        return PcbPlacementState.WaitingForPcb;
    }

    private PcbPlacementState? FinishPlacementState(HeatSinkSlot heatSink)
    {
        if (HeatSinkCompleted(heatSink))
        {
            if (handler.Ipm != PlacementCylinderState.Up)
            {
                return PcbPlacementState.RaisingIpm;
            }

            if (handler.Handler != PlacementCylinderState.Up)
            {
                return PcbPlacementState.RaisingHandler;
            }

            return handler.AtHorizontalZ
                ? null
                : PcbPlacementState.RaisingZ;
        }

        if (handler.Ipm == PlacementCylinderState.Down)
        {
            return PcbPlacementState.RecordingPlacement;
        }

        if (handler.Ipm != PlacementCylinderState.Up)
        {
            return PcbPlacementState.RaisingIpm;
        }

        return handler.Gripper == PlacementGripperState.Open
            ? PcbPlacementState.PressingPcb
            : PcbPlacementState.OpeningGripper;
    }

    private HeatSinkSlot? NextHeatSink()
    {
        if (work.HeatSinkPresent(HeatSinkSlot.HeatSink1)
            && !HeatSinkCompleted(HeatSinkSlot.HeatSink1))
        {
            return HeatSinkSlot.HeatSink1;
        }

        return work.HeatSinkPresent(HeatSinkSlot.HeatSink2)
               && !HeatSinkCompleted(HeatSinkSlot.HeatSink2)
            ? HeatSinkSlot.HeatSink2
            : null;
    }

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

    private static AxisPos HeatSinkPosition(
        PcbPlacementRecipe recipe,
        HeatSinkSlot heatSink) =>
        heatSink == HeatSinkSlot.HeatSink1
            ? recipe.HeatSink1PcbPlacementPosition
            : recipe.HeatSink2PcbPlacementPosition;
}
