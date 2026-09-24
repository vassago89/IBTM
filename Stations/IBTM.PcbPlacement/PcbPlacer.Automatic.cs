using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.PcbPlacement;

public sealed partial class PcbPlacer
{
    public async Task RunAsync(
        CancellationToken cancellationToken = default,
        bool repeat = false)
    {
        _repeat = repeat;
        _runTargets = null;
        try
        {
            BeginRun(SequenceStep ?? PcbPlacementState.MovingToHandoff);
            _supply.Changed += OnChanged;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_work.Station.CarrierPresent || _work.Completed)
                {
                    _runTargets = null;
                }
                if (_work.Enabled && _repeat && !_units.MainConveyor && _work.Completed
                    && _work.Station.CarrierSeated && State == PcbPlacementState.WaitingForCarrier)
                    _work.StartRepeat(_work.CurrentJob);
                if (_work.Station.CarrierSeated)
                {
                    _runTargets ??= Enum.GetValues<HeatSinkSlot>().Where(_work.Station.IsHeatSinkPresent).ToArray();
                }

                var heatSink = TargetHeatSink;
                var step = GetNextStep(heatSink, repeat);
                if (!await ExecuteStepAsync(step, heatSink, cancellationToken, repeat))
                    await WaitForChangeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _supply.Changed -= OnChanged;
            _runTargets = null;
            _repeatTrip = null;
            _repeat = false;
            EndRun(cancellationToken);
        }
    }

    public PcbPlacementState GetNextStep(HeatSinkSlot? heatSink, bool repeat = false)
    {
        var state = State;
        if (repeat && _work.Enabled && _repeatTrip is null)
        {
            if (state == PcbPlacementState.CompletingCarrier)
                return state;
            if (!_work.Station.CarrierSeated || _work.Completed)
                return PcbPlacementState.WaitingForCarrier;
            return heatSink is null ? PcbPlacementState.CompletingCarrier : PcbPlacementState.PickingPcb;
        }
        switch (state)
        {
            case PcbPlacementState.WaitingForSupplyReceipt when _supply.Handoff == PcbSupplyHandoff.Released:
                return PcbPlacementState.PresentingToSupply;
            case PcbPlacementState.WaitingForSupplyGrip when _supply.Handoff == PcbSupplyHandoff.Holding:
                return PcbPlacementState.ReleasingToSupply;
            case PcbPlacementState.WaitingForSupplyDeparture when _supply.Handoff == PcbSupplyHandoff.Unavailable:
                return PcbPlacementState.MovingToHandoff;
            case PcbPlacementState.WaitingForSupply when _supply.Handoff == PcbSupplyHandoff.Holding:
                return PcbPlacementState.ReceivingPcb;
            case PcbPlacementState.WaitingForSupplyRelease when _supply.Handoff == PcbSupplyHandoff.Released:
                return PcbPlacementState.PreparingPlacement;
            case PcbPlacementState.WaitingForCarrier when _work.Station.CarrierSeated && !_work.Completed:
                return heatSink is null ? PcbPlacementState.CompletingCarrier : PcbPlacementState.PlacingPcb;
            default:
                return state;
        }
    }

    public async Task<bool> ExecuteStepAsync(
        PcbPlacementState state,
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken,
        bool repeat = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (state == PcbPlacementState.Disabled || !_work.Enabled)
        {
            if (_work.Station.CarrierSeated)
                _work.Complete(_work.CurrentJob);
            EnterStep(PcbPlacementState.Disabled, workId: _work.CurrentJob.Id,
                waitingFor: _work.Completed ? "carrier transfer" : "carrier seated");
            return false;
        }
        var job = _repeatTrip?.Job ?? _work.CurrentJob;
        State = state;
        EnterStep(state, heatSink?.ToString(), job.Id);
        if (IsPcbGripUncertain
            || state is PcbPlacementState.ReturningToSupply or PcbPlacementState.WaitingForSupplyReceipt
                or PcbPlacementState.PresentingToSupply or PcbPlacementState.WaitingForSupplyGrip
                && !PcbSecured)
            throw new InvalidOperationException("Placement PCB holding is uncertain away from a confirmed support. Check vacuum and PCB detection before moving or releasing it.");
        if (repeat && state == PcbPlacementState.PickingPcb && _repeatTrip is null)
            _repeatTrip = new(job, heatSink ?? throw new InvalidOperationException("No repeat PCB is selected."));

        var callerToken = cancellationToken;
        using var repeatOperation = _repeatTrip is null ? null
            : CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        var requiresHolding = PcbSecured;
        void CheckRepeatFeedback()
        {
            if (!_work.Station.CarrierSeated || !ReferenceEquals(job, _work.CurrentJob)
                || requiresHolding && !PcbSecured)
                repeatOperation?.Cancel();
        }
        if (repeatOperation is not null)
        {
            Changed += CheckRepeatFeedback;
            cancellationToken = repeatOperation.Token;
        }
        try
        {
            if (repeatOperation is not null)
                CheckRepeatFeedback();
            cancellationToken.ThrowIfCancellationRequested();
            switch (state)
            {
                case PcbPlacementState.PickingPcb:
                    var pickPosition = GetHeatSinkPosition(heatSink!.Value);
                    await SetIpmLiftDownAsync(false, cancellationToken);
                    await SetLiftDownAsync(false, cancellationToken);
                    await MoveToXYAsync(pickPosition, cancellationToken);
                    await MoveAxisAsync(MotionAxis.Z, pickPosition.Z, cancellationToken);
                    await SetLiftDownAsync(true, cancellationToken);
                    await WaitForPcbAsync(cancellationToken);
                    await SetVacuumAsync(true, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!PcbSecured)
                        throw new InvalidOperationException("Repeat pickup requires both PCB detection and vacuum before raising the handler.");
                    State = PcbPlacementState.ReturningToSupply;
                    break;
                case PcbPlacementState.ReturningToSupply:
                    if (!PcbSecured)
                        throw new InvalidOperationException("Returning a PCB requires confirmed holding feedback.");
                    await SetLiftDownAsync(false, cancellationToken);
                    await PrepareHandoffAsync(cancellationToken, returning: true);
                    if (!_units.PcbSupply)
                        State = PcbPlacementState.PreparingPlacement;
                    break;
                case PcbPlacementState.PresentingToSupply:
                    await PrepareReceiptAsync(cancellationToken, returning: true);
                    break;
                case PcbPlacementState.ReleasingToSupply:
                    if (_supply.Handoff != PcbSupplyHandoff.Holding)
                        throw new InvalidOperationException("Supply must hold the returned PCB before placement releases it.");
                    requiresHolding = false;
                    await SetVacuumAsync(false, cancellationToken);
                    if (_supply.Handoff != PcbSupplyHandoff.Holding)
                        throw new InvalidOperationException("Supply lost the returned PCB while placement released vacuum.");
                    await SetLiftDownAsync(false, cancellationToken);
                    await MoveToHorizontalZAsync(cancellationToken);
                    await MoveAxisAsync(MotionAxis.Y, GetHeatSinkPosition(heatSink!.Value).Y, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var departure = _motion.GetPosition();
                    _handoffPosition = new() { X = departure.X, Y = departure.Y, Z = departure.Z };
                    State = PcbPlacementState.WaitingForSupplyDeparture;
                    break;
                case PcbPlacementState.MovingToHandoff:
                    await SetLiftDownAsync(false, cancellationToken);
                    await MoveToHorizontalZAsync(cancellationToken);
                    await SetIpmLiftDownAsync(!_repeat, cancellationToken);
                    await PrepareHandoffAsync(cancellationToken);
                    break;
                case PcbPlacementState.ReceivingPcb:
                    if (!await ReceivePcbAsync(cancellationToken))
                        return false;
                    break;
                case PcbPlacementState.PreparingPlacement:
                    await PreparePlacementAsync(heatSink ?? HeatSinkSlot.HeatSink1, cancellationToken);
                    if (PcbSecured)
                        State = _work.Station.CarrierSeated && !_work.Completed
                            ? PcbPlacementState.PlacingPcb : PcbPlacementState.WaitingForCarrier;
                    else
                    {
                        State = TargetHeatSink is null && _work.Station.CarrierSeated && !_work.Completed
                            ? PcbPlacementState.CompletingCarrier : PcbPlacementState.MovingToHandoff;
                    }
                    break;
                case PcbPlacementState.PlacingPcb:
                {
                    var target = heatSink ?? throw new InvalidOperationException("No placement target is selected.");
                    var carryingPcb = true;
                    if (!PcbSecured)
                        throw new InvalidOperationException("Placement requires confirmed PCB holding before travelling to its target.");
                    // Presence is required through placement/press completion, until retraction.
                    var checkingPcbPresence = false;
                    using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    void CheckPlacementFeedback()
                    {
                        if (!_work.Station.CarrierSeated || !ReferenceEquals(job, _work.CurrentJob)
                            || carryingPcb && !PcbSecured
                            || checkingPcbPresence && Pcb == PlacementPcbState.None)
                            operation.Cancel();
                    }
                    Changed += CheckPlacementFeedback;
                    try
                    {
                        CheckPlacementFeedback();
                        operation.Token.ThrowIfCancellationRequested();
                        var position = GetHeatSinkPosition(target);
                        await SetIpmLiftDownAsync(!_repeat, operation.Token);
                        await SetLiftDownAsync(false, operation.Token);
                        await MoveToHorizontalZAsync(operation.Token);
                        await MoveAxisAsync(MotionAxis.Y, position.Y, operation.Token);
                        await MoveAxisAsync(MotionAxis.X, position.X, operation.Token);
                        await MoveAxisAsync(MotionAxis.Z, position.Z, operation.Token);
                        await SetLiftDownAsync(true, operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        // Grip is no longer required after confirmed placement descent.
                        carryingPcb = false;
                        requiresHolding = false;
                        await SetVacuumAsync(false, operation.Token);

                        await SetIpmLiftDownAsync(false, operation.Token);
                        checkingPcbPresence = true;
                        CheckPlacementFeedback();
                        operation.Token.ThrowIfCancellationRequested();
                        if (!_repeat)
                            await SetIpmLiftDownAsync(true, operation.Token);
                        CheckPlacementFeedback();
                        operation.Token.ThrowIfCancellationRequested();
                        _work.GetAssembly(job, target);
                        checkingPcbPresence = false;
                        _repeatTrip = null;
                        await SetIpmLiftDownAsync(false, operation.Token);
                        await SetLiftDownAsync(false, operation.Token);
                        await MoveToHorizontalZAsync(operation.Token);
                        State = TargetHeatSink is null
                            ? PcbPlacementState.CompletingCarrier : PcbPlacementState.MovingToHandoff;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new InvalidOperationException("Placement lost PCB grip, PCB presence during pressing, or the original seated carrier.");
                    }
                    finally
                    {
                        Changed -= CheckPlacementFeedback;
                    }
                    break;
                }
                case PcbPlacementState.CompletingCarrier:
                    await PreparePlacementAsync(null, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    _work.Complete(job);
                    State = _repeat ? PcbPlacementState.WaitingForCarrier : PcbPlacementState.MovingToHandoff;
                    break;
                default:
                    return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (repeatOperation?.IsCancellationRequested == true
            && !callerToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The repeat PCB lost its original seated carrier or holding feedback.");
        }
        finally
        {
            if (repeatOperation is not null)
                Changed -= CheckRepeatFeedback;
        }
    }

    private async Task<bool> ReceivePcbAsync(CancellationToken cancellationToken)
    {
        if (_supply.Handoff != PcbSupplyHandoff.Holding)
            return false;
        using var receipt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckSupplyHolding()
        {
            if (_supply.Handoff != PcbSupplyHandoff.Holding && !PcbSecured)
                receipt.Cancel();
        }
        _supply.Changed += CheckSupplyHolding;
        Changed += CheckSupplyHolding;
        try
        {
            CheckSupplyHolding();
            receipt.Token.ThrowIfCancellationRequested();
            var ipmDown = !_repeat;
            if (IpmLift != (ipmDown ? PlacementCylinderState.Down : PlacementCylinderState.Up))
                await SetIpmLiftDownAsync(ipmDown, receipt.Token);
            await PrepareReceiptAsync(receipt.Token);
            await WaitForPcbAsync(receipt.Token);
            await SetVacuumAsync(true, receipt.Token);
            receipt.Token.ThrowIfCancellationRequested();
            State = PcbPlacementState.WaitingForSupplyRelease;
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Supply lost confirmed PCB holding during receipt.");
        }
        finally
        {
            _supply.Changed -= CheckSupplyHolding;
            Changed -= CheckSupplyHolding;
        }
    }

    private async Task PreparePlacementAsync(HeatSinkSlot? departure, CancellationToken cancellationToken)
    {
        var carryingPcb = PcbSecured;
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckHolding()
        {
            if (carryingPcb && !PcbSecured)
                preparation.Cancel();
        }
        Changed += CheckHolding;
        try
        {
            CheckHolding();
            preparation.Token.ThrowIfCancellationRequested();
            await SetIpmLiftDownAsync(carryingPcb && !_repeat, preparation.Token);
            await SetLiftDownAsync(false, preparation.Token);
            await MoveToHorizontalZAsync(preparation.Token);
            if (carryingPcb && departure is { } destination)
                await MoveAxisAsync(MotionAxis.Y, GetHeatSinkPosition(destination).Y, preparation.Token);
            preparation.Token.ThrowIfCancellationRequested();
            var position = _motion.GetPosition();
            _handoffPosition = new() { X = position.X, Y = position.Y, Z = position.Z };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Placement lost PCB grip while raising or leaving handoff.");
        }
        finally
        {
            Changed -= CheckHolding;
        }
    }

    public async Task PrepareHandoffAsync(CancellationToken cancellationToken = default, bool returning = false)
    {
        State = returning ? PcbPlacementState.ReturningToSupply : PcbPlacementState.MovingToHandoff;
        await MoveToHorizontalZAsync(cancellationToken);
        await MoveAxisAsync(MotionAxis.X, _settings.HandoffPosition.X, cancellationToken);
        await MoveAxisAsync(MotionAxis.Y, _settings.HandoffPosition.Y, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _handoffPosition = new()
        {
            X = _settings.HandoffPosition.X, Y = _settings.HandoffPosition.Y, Z = _settings.HandoffPosition.Z,
        };
        State = returning ? PcbPlacementState.WaitingForSupplyReceipt : PcbPlacementState.WaitingForSupply;
    }

    public async Task PrepareReceiptAsync(CancellationToken cancellationToken = default, bool returning = false)
    {
        State = returning ? PcbPlacementState.PresentingToSupply : PcbPlacementState.ReceivingPcb;
        await MoveAxisAsync(
            MotionAxis.Z,
            _settings.ReceiveZ ?? throw new MotionInterlockException("Teach PCB Receive Z before receiving a PCB."),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _handoffPosition = new()
        {
            X = _settings.HandoffPosition.X, Y = _settings.HandoffPosition.Y, Z = _settings.ReceiveZ!.Value,
        };
        if (PcbSecured)
            State = returning ? PcbPlacementState.WaitingForSupplyGrip : PcbPlacementState.WaitingForSupplyRelease;
    }
}
