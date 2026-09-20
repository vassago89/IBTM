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

    public HeatSinkSlot? ReturningPcb => _repeatTrip is { } trip
        && trip.State is PcbPlacementState.MovingToHandoff or PcbPlacementState.ReceivingPcb
        ? trip.HeatSink : null;

    private async Task ExecuteRepeatAsync(
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken)
    {
        if (_repeatTrip is null)
        {
            if (IsPcbGripUncertain)
                throw new InvalidOperationException("Placement PCB holding is uncertain away from a confirmed support. Check vacuum and PCB detection before restarting Repeat.");
            if (_work.Station.CarrierSeated && !_work.Completed && heatSink is not null)
            {
                _repeatTrip = new(_work.CurrentJob, heatSink.Value);
                // STOP discards direction. A PCB already held by either handler goes forward.
                if (PcbSecured)
                    _repeatTrip.State = _units.PcbSupply && IsAtReceivePosition()
                        ? PcbPlacementState.WaitingForSupply : PcbPlacementState.PlacingPcb;
                else if (_units.PcbSupply && _supply.PcbSecured)
                    _repeatTrip.State = PcbPlacementState.WaitingForSupply;
                Changed?.Invoke();
            }
        }

        if (_repeatTrip is not { } trip)
        {
            if (!await PlaceAsync(heatSink, cancellationToken))
                await WaitForChangeAsync(cancellationToken);
            return;
        }

        _work.RequireCurrentJob(trip.Job);
        if (!_work.Station.CarrierSeated)
            throw new InvalidOperationException("The repeat PCB carrier is no longer seated.");

        if (trip.State == PcbPlacementState.PickingPcb && PcbSecured)
            trip.State = PcbPlacementState.MovingToHandoff;

        if (trip.State == PcbPlacementState.MovingToHandoff)
        {
            if (!PcbSecured)
                throw new InvalidOperationException("The repeat PCB lost its holding feedback before reaching handoff.");
            if (HandlerRaised && IsAtHorizontalZ() && IsAtHandoffXY() && !_units.PcbSupply)
                trip.State = PcbPlacementState.PlacingPcb;
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiving = trip.State == PcbPlacementState.WaitingForSupply && PcbSecured;
        void CheckRepeatFeedback()
        {
            if (!_work.Station.CarrierSeated
                || !ReferenceEquals(trip.Job, _work.CurrentJob)
                || (receiving
                    || trip.State is PcbPlacementState.MovingToHandoff or PcbPlacementState.ReceivingPcb or PcbPlacementState.PlacingPcb)
                    && !PcbSecured)
                operation.Cancel();
        }

        Changed += CheckRepeatFeedback;
        try
        {
            CheckRepeatFeedback();
            operation.Token.ThrowIfCancellationRequested();
            if (IpmLift != PlacementCylinderState.Up)
                await SetIpmLiftDownAsync(false, operation.Token);
            if (trip.State == PcbPlacementState.PickingPcb)
            {
                TraceStep(trip.State, trip.HeatSink.ToString(), trip.Job.Id);
                var pickPosition = GetHeatSinkPosition(trip.HeatSink);
                await SetLiftDownAsync(false, operation.Token);
                await MoveToXYAsync(pickPosition, operation.Token);
                await MoveAxisAsync(MotionAxis.Z, pickPosition.Z, operation.Token);
                await SetLiftDownAsync(true, operation.Token);
                await WaitForPcbAsync(operation.Token);
                await SetVacuumAsync(true, operation.Token);
                return;
            }
            if (_units.PcbSupply && trip.State == PcbPlacementState.MovingToHandoff
                && HandlerRaised && IsAtHorizontalZ() && IsAtHandoffXY())
            {
                Changed?.Invoke();
                while (_supply.Handoff != PcbSupplyHandoff.Released)
                    await WaitForChangeAsync(operation.Token);
                trip.State = PcbPlacementState.ReceivingPcb;
                TraceStep(trip.State, $"Return {trip.HeatSink} to Supply", trip.Job.Id);
                await MoveToReceiveZAsync(operation.Token);
                Changed?.Invoke();
                while (_supply.Handoff != PcbSupplyHandoff.Holding)
                    await WaitForChangeAsync(operation.Token);
                trip.State = PcbPlacementState.WaitingForSupplyRelease;
                await SetVacuumAsync(false, operation.Token);
                if (_supply.Handoff != PcbSupplyHandoff.Holding)
                    throw new InvalidOperationException("Supply lost the returned PCB while placement released vacuum.");
                await SetLiftDownAsync(false, operation.Token);
                await MoveToHorizontalZAsync(operation.Token);
                await MoveAxisAsync(MotionAxis.Y, GetHeatSinkPosition(trip.HeatSink).Y, operation.Token);
                trip.State = PcbPlacementState.WaitingForSupply;
                Changed?.Invoke();
                // Observe departure before accepting the next forward handoff.
                while (_supply.Handoff != PcbSupplyHandoff.Unavailable)
                    await WaitForChangeAsync(operation.Token);
            }
            if (trip.State == PcbPlacementState.WaitingForSupply)
            {
                if (!PcbSecured)
                {
                    await SetLiftDownAsync(false, operation.Token);
                    await MoveToHandoffXYAsync(operation.Token);
                    while (_supply.Handoff != PcbSupplyHandoff.Holding)
                        await WaitForChangeAsync(operation.Token);
                    TraceStep(PcbPlacementState.ReceivingPcb, trip.HeatSink.ToString(), trip.Job.Id);
                    if (!await ReceivePcbAsync(operation.Token))
                        return;
                }
                receiving = true;
                CheckRepeatFeedback();
                operation.Token.ThrowIfCancellationRequested();
                while (_supply.Handoff != PcbSupplyHandoff.Released)
                    await WaitForChangeAsync(operation.Token);
                await SetLiftDownAsync(false, operation.Token);
                await MoveToHorizontalZAsync(operation.Token);
                await MoveAxisAsync(MotionAxis.Y, GetHeatSinkPosition(trip.HeatSink).Y, operation.Token);
                trip.State = PcbPlacementState.PlacingPcb;
                Changed?.Invoke();
                receiving = false;
            }
            if (!await PlaceAsync(trip.HeatSink, operation.Token))
                await WaitForChangeAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The repeat PCB lost its original seated carrier or holding feedback.");
        }
        finally
        {
            Changed -= CheckRepeatFeedback;
        }
    }

    private sealed class RepeatPcbTrip
    {
        public RepeatPcbTrip(StationWork.Job job, HeatSinkSlot heatSink)
        {
            Job = job;
            HeatSink = heatSink;
            State = PcbPlacementState.PickingPcb;
        }

        public StationWork.Job Job { get; }
        public HeatSinkSlot HeatSink { get; }
        public PcbPlacementState State { get; set; }
    }
}
