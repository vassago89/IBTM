using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed partial class PcbPlacer
{
    private bool _repeat;
    // Ownership for the current Repeat run.
    private RepeatPcbTrip? _repeatTrip;

    public HeatSinkSlot? ReturningPcb => _repeat && _repeatTrip is { } trip
        && trip.State is PcbPlacementState.MovingToHandoff or PcbPlacementState.ReceivingPcb
        ? trip.HeatSink : null;

    private async Task ExecuteRepeatAsync(
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken)
    {
        if (_repeatTrip is null && State == PcbPlacementState.CompletingCarrier)
        {
            await PlaceAsync(heatSink, cancellationToken);
            return;
        }
        if (_repeatTrip is null)
        {
            if (IsPcbGripUncertain)
                throw new InvalidOperationException("Placement PCB holding requires both PCB detection and vacuum feedback.");
            if (_work.Station.CarrierSeated && !_work.Completed && heatSink is not null)
            {
                _repeatTrip = new(_work.CurrentJob, heatSink.Value);
                Changed?.Invoke();
            }
        }

        if (_repeatTrip is not { } trip)
        {
            State = PcbPlacementState.WaitingForCarrier;
            await WaitForChangeAsync(cancellationToken);
            return;
        }

        _work.RequireCurrentJob(trip.Job);
        if (!_work.Station.CarrierSeated)
            throw new InvalidOperationException("The repeat PCB carrier is no longer seated.");

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
                State = PcbPlacementState.PickingPcb;
                TraceStep(trip.State, trip.HeatSink.ToString(), trip.Job.Id);
                var pickPosition = GetHeatSinkPosition(trip.HeatSink);
                await SetLiftDownAsync(false, operation.Token);
                await MoveToXYAsync(pickPosition, operation.Token);
                await MoveAxisAsync(MotionAxis.Z, pickPosition.Z, operation.Token);
                await SetLiftDownAsync(true, operation.Token);
                await WaitForPcbAsync(operation.Token);
                await SetVacuumAsync(true, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (!PcbSecured)
                    throw new InvalidOperationException("Repeat pickup requires both PCB detection and vacuum before raising the handler.");
                trip.State = PcbPlacementState.MovingToHandoff;
                Changed?.Invoke();
                return;
            }
            if (trip.State == PcbPlacementState.MovingToHandoff)
            {
                TraceStep(trip.State, trip.HeatSink.ToString(), trip.Job.Id);
                await SetLiftDownAsync(false, operation.Token);
                await MoveToHandoffXYAsync(operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                trip.State = _units.PcbSupply
                    ? PcbPlacementState.ReceivingPcb : PcbPlacementState.PlacingPcb;
                Changed?.Invoke();
            }
            if (trip.State == PcbPlacementState.ReceivingPcb)
            {
                while (_supply.Handoff != PcbSupplyHandoff.Released)
                    await WaitForChangeAsync(operation.Token);
                TraceStep(trip.State, $"Return {trip.HeatSink} to Supply", trip.Job.Id);
                await MoveToReceiveZAsync(operation.Token);
                Changed?.Invoke();
                while (_supply.Handoff != PcbSupplyHandoff.Holding)
                    await WaitForChangeAsync(operation.Token);
                trip.State = PcbPlacementState.WaitingForSupplyRelease;
                await SetVacuumAsync(false, operation.Token);
                if (_supply.Handoff != PcbSupplyHandoff.Holding)
                    throw new InvalidOperationException("Supply lost the returned PCB while placement released vacuum.");
                State = PcbPlacementState.PreparingPlacement;
                await SetLiftDownAsync(false, operation.Token);
                await MoveToHorizontalZAsync(operation.Token);
                await MoveAxisAsync(MotionAxis.Y, GetHeatSinkPosition(trip.HeatSink).Y, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                _handoffReady = true;
                trip.State = PcbPlacementState.WaitingForSupply;
                State = PcbPlacementState.WaitingForSupply;
                // Observe departure before accepting the next forward handoff.
                while (_supply.Handoff != PcbSupplyHandoff.Unavailable)
                    await WaitForChangeAsync(operation.Token);
            }
            if (trip.State == PcbPlacementState.WaitingForSupply)
            {
                await SetLiftDownAsync(false, operation.Token);
                await MoveToHandoffXYAsync(operation.Token);
                while (_supply.Handoff != PcbSupplyHandoff.Holding)
                    await WaitForChangeAsync(operation.Token);
                TraceStep(PcbPlacementState.ReceivingPcb, trip.HeatSink.ToString(), trip.Job.Id);
                if (!await ReceivePcbAsync(operation.Token))
                    return;
                receiving = true;
                CheckRepeatFeedback();
                operation.Token.ThrowIfCancellationRequested();
                while (_supply.Handoff != PcbSupplyHandoff.Released)
                    await WaitForChangeAsync(operation.Token);
                State = PcbPlacementState.PreparingPlacement;
                await SetLiftDownAsync(false, operation.Token);
                await MoveToHorizontalZAsync(operation.Token);
                await MoveAxisAsync(MotionAxis.Y, GetHeatSinkPosition(trip.HeatSink).Y, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                _handoffReady = true;
                trip.State = PcbPlacementState.PlacingPcb;
                State = PcbPlacementState.PlacingPcb;
                receiving = false;
            }
            if (trip.State == PcbPlacementState.PlacingPcb
                && State is PcbPlacementState.WaitingForSupply or PcbPlacementState.WaitingForCarrier)
                State = PcbPlacementState.PreparingPlacement;
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
