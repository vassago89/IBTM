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
    private event Action? RepeatChanged;

    public HeatSinkSlot? ReturningPcb => _repeatTrip is { } trip
        && trip.State is PcbPlacementState.MovingToHandoff or PcbPlacementState.ReceivingPcb
        ? trip.HeatSink : null;

    private async Task ExecuteRepeatAsync(
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken)
    {
        if (_repeatTrip is null)
        {
            if (_work.Station.CarrierSeated && !_work.Completed && heatSink is not null)
            {
                _repeatTrip = new(_work.CurrentJob, heatSink.Value);
                // STOP discards direction. A PCB already held by either handler goes forward.
                if (_handler.PcbSecured)
                    _repeatTrip.State = _units.PcbSupply && _handler.IsAtReceivePosition()
                        ? PcbPlacementState.WaitingForSupply : PcbPlacementState.PlacingPcb;
                else if (_units.PcbSupply && _supply.PcbDetected)
                    _repeatTrip.State = PcbPlacementState.WaitingForSupply;
                RepeatChanged?.Invoke();
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

        if (trip.State == PcbPlacementState.PickingPcb && _handler.Pcb == PlacementPcbState.Secured)
            trip.State = PcbPlacementState.MovingToHandoff;

        if (trip.State == PcbPlacementState.MovingToHandoff)
        {
            if (_handler.Pcb != PlacementPcbState.Secured)
                throw new InvalidOperationException("The repeat PCB lost its holding feedback before reaching handoff.");
            if (_handler.Lift == PlacementCylinderState.Up
                && _handler.IsAtHorizontalZ()
                && _handler.IsAtHandoffXY())
            {
                if (!_units.PcbSupply)
                    trip.State = PcbPlacementState.PlacingPcb;
            }
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiving = trip.State == PcbPlacementState.WaitingForSupply && _handler.PcbSecured;
        void CheckRepeatFeedback()
        {
            if (!_work.Station.CarrierSeated
                || !ReferenceEquals(trip.Job, _work.CurrentJob)
                || (receiving
                    || trip.State is PcbPlacementState.MovingToHandoff or PcbPlacementState.ReceivingPcb or PcbPlacementState.PlacingPcb)
                    && _handler.Pcb != PlacementPcbState.Secured)
                operation.Cancel();
        }

        _work.Changed += CheckRepeatFeedback;
        _handler.Changed += CheckRepeatFeedback;
        try
        {
            CheckRepeatFeedback();
            operation.Token.ThrowIfCancellationRequested();
            if (trip.State == PcbPlacementState.PickingPcb)
            {
                TraceStep(trip.State, trip.HeatSink.ToString(), trip.Job.Id);
                var pickPosition = GetHeatSinkPosition(trip.HeatSink);
                await _handler.SetLiftDownAsync(false, operation.Token);
                await _handler.MoveToHorizontalZAsync(operation.Token);
                await _handler.MoveToXYAsync(pickPosition, operation.Token);
                await _handler.SetIpmGripperAsync(false, operation.Token);
                await _handler.SetIpmLiftDownAsync(true, operation.Token);
                await _handler.MoveAxisAsync(MotionAxis.Z, pickPosition.Z, operation.Token);
                await _handler.SetLiftDownAsync(true, operation.Token);
                await _handler.WaitForPcbAsync(operation.Token);
                await _handler.SetVacuumAsync(true, operation.Token);
                await _handler.SetIpmGripperAsync(true, operation.Token);
                return;
            }
            if (_units.PcbSupply && trip.State == PcbPlacementState.MovingToHandoff
                && _handler.HandlerRaised && _handler.IsAtHorizontalZ() && _handler.IsAtHandoffXY())
            {
                RepeatChanged?.Invoke();
                while (_supply.Handoff != PcbSupplyHandoff.Released)
                    await WaitForChangeAsync(operation.Token);
                trip.State = PcbPlacementState.ReceivingPcb;
                TraceStep(trip.State, $"Return {trip.HeatSink} to Supply", trip.Job.Id);
                await _handler.MoveToReceiveZAsync(operation.Token);
                RepeatChanged?.Invoke();
                while (_supply.Handoff != PcbSupplyHandoff.Holding)
                    await WaitForChangeAsync(operation.Token);
                trip.State = PcbPlacementState.WaitingForSupplyRelease;
                await _handler.SetVacuumAsync(false, operation.Token);
                if (_supply.Handoff != PcbSupplyHandoff.Holding)
                    throw new InvalidOperationException("Supply lost the returned PCB before placement released its gripper.");
                await _handler.SetIpmGripperAsync(false, operation.Token);
                await _handler.SetLiftDownAsync(false, operation.Token);
                await _handler.MoveToHorizontalZAsync(operation.Token);
                await _handler.MoveAxisAsync(MotionAxis.Y, GetHeatSinkPosition(trip.HeatSink).Y, operation.Token);
                trip.State = PcbPlacementState.WaitingForSupply;
                RepeatChanged?.Invoke();
                // Observe departure before accepting the next forward handoff.
                while (_supply.Handoff != PcbSupplyHandoff.Unavailable)
                    await WaitForChangeAsync(operation.Token);
            }
            if (trip.State == PcbPlacementState.WaitingForSupply)
            {
                if (!_handler.PcbSecured)
                {
                    await _handler.SetLiftDownAsync(false, operation.Token);
                    await _handler.MoveToHorizontalZAsync(operation.Token);
                    await _handler.SetIpmGripperAsync(false, operation.Token);
                    await _handler.SetIpmLiftDownAsync(true, operation.Token);
                    await _handler.MoveToHandoffXYAsync(operation.Token);
                    while (_supply.Handoff != PcbSupplyHandoff.Holding)
                        await WaitForChangeAsync(operation.Token);
                    TraceStep(PcbPlacementState.ReceivingPcb, trip.HeatSink.ToString(), trip.Job.Id);
                    await _handler.MoveToReceiveZAsync(operation.Token);
                    await _handler.WaitForPcbAsync(operation.Token);
                    await _handler.SetVacuumAsync(true, operation.Token);
                    await _handler.SetIpmGripperAsync(true, operation.Token);
                }
                receiving = true;
                CheckRepeatFeedback();
                operation.Token.ThrowIfCancellationRequested();
                while (_supply.Handoff != PcbSupplyHandoff.Released)
                    await WaitForChangeAsync(operation.Token);
                await _handler.SetLiftDownAsync(false, operation.Token);
                await _handler.MoveToHorizontalZAsync(operation.Token);
                await _handler.MoveAxisAsync(MotionAxis.Y, GetHeatSinkPosition(trip.HeatSink).Y, operation.Token);
                trip.State = PcbPlacementState.PlacingPcb;
                RepeatChanged?.Invoke();
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
            _work.Changed -= CheckRepeatFeedback;
            _handler.Changed -= CheckRepeatFeedback;
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
