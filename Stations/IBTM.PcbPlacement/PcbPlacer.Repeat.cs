using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed partial class PcbPlacer
{
    private bool _repeat;
    // Current-run ownership only; discarded when RunAsync exits.
    private RepeatPcbTrip? _repeatTrip;

    private enum RepeatPcbPhase { Picking, ToHandoff, Placing, Releasing }

    private sealed class RepeatPcbTrip(StationWork.Job job, HeatSinkSlot heatSink)
    {
        public StationWork.Job Job { get; } = job;
        public HeatSinkSlot HeatSink { get; } = heatSink;
        public RepeatPcbPhase Phase { get; set; }
    }

    private async Task ExecuteRepeatAsync(
        PcbPlacementRecipe recipe,
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken)
    {
        if (_repeatTrip is null)
        {
            // The entry carrier already has its PCBs. Start by picking them on the first pass too.
            if (_work.CarrierSeated && !_work.Completed && heatSink is not null)
                _repeatTrip = new(_work.CurrentJob, heatSink.Value);
        }

        if (_repeatTrip is not { } trip)
        {
            await (PlaceStepAsync(recipe, heatSink, cancellationToken) ?? WaitForChangeAsync(cancellationToken));
            return;
        }

        _work.RequireCurrentJob(trip.Job);
        if (!_work.CarrierSeated)
            throw new InvalidOperationException("The repeat PCB carrier is no longer seated.");

        if (trip.Phase == RepeatPcbPhase.Picking && _handler.Pcb == PlacementPcbState.Secured)
            trip.Phase = RepeatPcbPhase.ToHandoff;

        if (trip.Phase == RepeatPcbPhase.ToHandoff)
        {
            if (_handler.Pcb != PlacementPcbState.Secured)
                throw new InvalidOperationException("The repeat PCB lost its holding feedback before reaching handoff.");
            if (_handler.Lift == PlacementCylinderState.Up
                && _handler.IsAtHorizontalZ()
                && _handler.Rotation == PlacementRotationState.Unrotated
                && _handler.IsAtBufferXY())
            {
                trip.Phase = RepeatPcbPhase.Placing;
            }
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckRepeatFeedback()
        {
            if (!_work.CarrierSeated
                || !ReferenceEquals(trip.Job, _work.CurrentJob)
                || trip.Phase is RepeatPcbPhase.ToHandoff or RepeatPcbPhase.Placing
                    && _handler.Pcb != PlacementPcbState.Secured)
                operation.Cancel();
        }

        _work.Changed += CheckRepeatFeedback;
        _handler.Changed += CheckRepeatFeedback;
        try
        {
            CheckRepeatFeedback();
            operation.Token.ThrowIfCancellationRequested();
            await (PlaceStepAsync(recipe, trip.HeatSink, operation.Token) ?? WaitForChangeAsync(operation.Token));
            operation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The repeat PCB lost its original seated carrier or holding feedback.");
        }
        finally
        {
            _work.Changed -= CheckRepeatFeedback;
            _handler.Changed -= CheckRepeatFeedback;
        }
    }

    private PcbPlacementState RepeatPickupState(PcbPlacementRecipe recipe, RepeatPcbTrip trip, bool live)
    {
        if (trip.Phase == RepeatPcbPhase.ToHandoff)
        {
            if (_handler.IpmLift != PlacementCylinderState.Down)
                return PcbPlacementState.LoweringIpm;
            if (_handler.Lift != PlacementCylinderState.Up)
                return PcbPlacementState.RaisingHandler;
            if (!_handler.IsAtHorizontalZ(live))
                return PcbPlacementState.RaisingZ;
            if (_handler.Rotation != PlacementRotationState.Unrotated)
                return PcbPlacementState.UnrotatingForBuffer;
            return PcbPlacementState.MovingAboveBuffer;
        }

        var position = HeatSinkPosition(recipe, trip.HeatSink);
        if (!_handler.IsAtXY(position, live)
            || _handler.Rotation != PlacementRotationState.Rotated
            || !_handler.IsAtZ(position, live))
        {
            if (_handler.Lift != PlacementCylinderState.Up)
                return PcbPlacementState.RaisingHandler;
            if (!_handler.IsAtHorizontalZ(live))
                return PcbPlacementState.RaisingZ;
            if (_handler.Rotation != PlacementRotationState.Rotated)
            {
                return _handler.IsAtXY(recipe.HeatSink1PcbPlacementPosition, live)
                    ? PcbPlacementState.RotatingForPlacement
                    : PcbPlacementState.MovingToWaitPosition;
            }
            if (!_handler.IsAtXY(position, live))
                return PcbPlacementState.MovingAboveHeatSink;
            if (_handler.IpmGripper != PlacementGripperState.Open)
                return PcbPlacementState.OpeningGripper;
            if (_handler.IpmLift != PlacementCylinderState.Down)
                return PcbPlacementState.LoweringIpm;
            return PcbPlacementState.LoweringToHeatSink;
        }

        if (!_handler.VacuumDetected && _handler.IpmGripper != PlacementGripperState.Open)
            return PcbPlacementState.OpeningGripper;
        if (_handler.IpmLift != PlacementCylinderState.Down)
            return PcbPlacementState.LoweringIpm;
        if (_handler.Lift != PlacementCylinderState.Down)
            return PcbPlacementState.LoweringHandler;
        if (_handler.Pcb == PlacementPcbState.None)
            return PcbPlacementState.WaitingForPcbDetection;
        if (!_handler.VacuumDetected)
            return PcbPlacementState.ApplyingVacuum;
        return PcbPlacementState.ClosingGripper;
    }
}
