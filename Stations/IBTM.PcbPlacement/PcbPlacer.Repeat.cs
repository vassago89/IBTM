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

    private async Task ExecuteRepeatAsync(
        PcbPlacementRecipe recipe,
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken)
    {
        if (_repeatTrip is null)
        {
            // The entry carrier already has its PCBs. Start by picking them on the first pass too.
            if (_work.Station.CarrierSeated && !_work.Completed && heatSink is not null)
                _repeatTrip = new(_work.CurrentJob, heatSink.Value);
        }

        if (_repeatTrip is not { } trip)
        {
            if (!await PlaceAsync(recipe, heatSink, cancellationToken))
                await WaitForChangeAsync(cancellationToken);
            return;
        }

        _work.RequireCurrentJob(trip.Job);
        if (!_work.Station.CarrierSeated)
            throw new InvalidOperationException("The repeat PCB carrier is no longer seated.");

        if (trip.Phase == RepeatPcbPhase.Picking && _handler.Pcb == PlacementPcbState.Secured)
            trip.Phase = RepeatPcbPhase.ToHandoff;

        if (trip.Phase == RepeatPcbPhase.ToHandoff)
        {
            if (_handler.Pcb != PlacementPcbState.Secured)
                throw new InvalidOperationException("The repeat PCB lost its holding feedback before reaching handoff.");
            if (_handler.Lift == PlacementCylinderState.Up
                && _handler.IsAtHorizontalZ()
                && _handler.IsAtBufferXY())
            {
                trip.Phase = RepeatPcbPhase.Placing;
            }
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckRepeatFeedback()
        {
            if (!_work.Station.CarrierSeated
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
            if (!await PlaceAsync(recipe, trip.HeatSink, operation.Token))
                await WaitForChangeAsync(operation.Token);
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

    private enum RepeatPcbPhase { Picking, ToHandoff, Placing, Releasing }

    private sealed class RepeatPcbTrip
    {
        public RepeatPcbTrip(StationWork.Job job, HeatSinkSlot heatSink)
        {
            Job = job;
            HeatSink = heatSink;
        }

        public StationWork.Job Job { get; }
        public HeatSinkSlot HeatSink { get; }
        public RepeatPcbPhase Phase { get; set; }
    }
}
