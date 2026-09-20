using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;

namespace IBTM.PcbPlacement;

public sealed partial class PcbPlacer : AutoUnit
{
    private readonly BufferStage _buffer;
    private readonly PcbPlacementHandler _handler;
    private readonly PcbPlacementWork _work;
    private HeatSinkSlot[]? _runTargets;
    // Down before release and Down after pressing have identical IO feedback.
    // Keep the press target only while this run owns the operation.
    private HeatSinkSlot? _pressingHeatSink;

    public PcbPlacer(BufferStage buffer, PcbPlacementHandler handler, PcbPlacementWork work)
    {
        _buffer = buffer;
        _handler = handler;
        _work = work;
        _work.Station.CarrierChanged += OnCarrierChanged;
    }

    public override event Action? Changed
    {
        add
        {
            _buffer.StateChanged += value;
            _handler.Changed += value;
            _work.Changed += value;
        }

        remove
        {
            _buffer.StateChanged -= value;
            _handler.Changed -= value;
            _work.Changed -= value;
        }
    }

    public HeatSinkSlot? TargetHeatSink
    {
        get
        {
            switch (true)
            {
                case true when _repeatTrip is { } trip:
                    return trip.HeatSink;
                case true when _work.Completed:
                    return null;
                case true when IsTarget(HeatSinkSlot.HeatSink1) && !IsHeatSinkCompleted(HeatSinkSlot.HeatSink1):
                    return HeatSinkSlot.HeatSink1;
                default:
                    return IsTarget(HeatSinkSlot.HeatSink2) && !IsHeatSinkCompleted(HeatSinkSlot.HeatSink2)
                        ? HeatSinkSlot.HeatSink2
                        : null;
            }
        }
    }

    public async Task RunAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken = default,
        bool repeat = false)
    {
        _repeat = repeat;
        _runTargets = null;
        try
        {
            BeginRun();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!_work.Enabled)
                    {
                        var job = _work.CurrentJob;
                        if (_work.Station.CarrierSeated)
                            _work.Complete(job);
                        TraceStep(PcbPlacementState.Disabled, workId: job.Id,
                            waitingFor: _work.Completed ? "carrier transfer" : "carrier seated");
                        await WaitForChangeAsync(cancellationToken);
                        continue;
                    }
                    await ExecuteAsync(recipe, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                EndRun(cancellationToken);
            }
        }
        finally
        {
            _runTargets = null;
            _pressingHeatSink = null;
            _repeatTrip = null;
            _repeat = false;
        }
    }

    private async Task ExecuteAsync(PcbPlacementRecipe recipe, CancellationToken cancellationToken)
    {
        if (!_work.Station.CarrierPresent || _work.Completed)
        {
            _runTargets = null;
        }
        else if (_work.Station.CarrierSeated)
        {
            _runTargets ??= Enum.GetValues<HeatSinkSlot>().Where(_work.Station.IsHeatSinkPresent).ToArray();
        }

        var heatSink = TargetHeatSink;
        if (_repeat)
            await ExecuteRepeatAsync(recipe, heatSink, cancellationToken);
        else if (!await PlaceAsync(recipe, heatSink, cancellationToken))
            await WaitForChangeAsync(cancellationToken);
    }

    // A state owns the whole operation. False means an external handoff/carrier wait.
    public async Task<bool> PlaceAsync(
        PcbPlacementRecipe recipe,
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = _work.CurrentJob;
        var state = GetState(recipe, heatSink);
        TraceStep(state, _repeatTrip is { } trip ? $"{trip.HeatSink}, Repeat {trip.Phase}" : heatSink?.ToString(), job.Id);
        switch (state)
        {
            case PcbPlacementState.MovingToHandoff:
                await _handler.SetLiftDownAsync(false, cancellationToken);
                await _handler.MoveToHorizontalZAsync(cancellationToken);
                if (_repeatTrip?.Phase != RepeatPcbPhase.ToHandoff)
                    await _handler.SetIpmGripperAsync(false, cancellationToken);
                await _handler.SetIpmLiftDownAsync(true, cancellationToken);
                await _handler.MoveAboveBufferAsync(cancellationToken);
                break;
            case PcbPlacementState.ReceivingPcb:
                if (!_buffer.IsPlacementEntryAllowed())
                    return false;
                if (_handler.IpmGripper != PlacementGripperState.Open)
                    await _handler.SetIpmGripperAsync(false, cancellationToken);
                if (_handler.IpmLift != PlacementCylinderState.Down)
                    await _handler.SetIpmLiftDownAsync(true, cancellationToken);
                if (!_buffer.IsPlacementEntryAllowed())
                    return false;
                await _handler.SetLiftDownAsync(true, cancellationToken);
                await _handler.WaitForPcbAsync(cancellationToken);
                await _handler.SetVacuumAsync(true, cancellationToken);
                await _handler.SetIpmGripperAsync(true, cancellationToken);
                break;
            case PcbPlacementState.PreparingPlacement:
                await _handler.SetIpmLiftDownAsync(_handler.Pcb == PlacementPcbState.Secured, cancellationToken);
                await _handler.SetLiftDownAsync(false, cancellationToken);
                await _handler.MoveToHorizontalZAsync(cancellationToken);
                break;
            case PcbPlacementState.PickingPcb:
                var pickPosition = GetHeatSinkPosition(recipe, heatSink!.Value);
                await _handler.SetLiftDownAsync(false, cancellationToken);
                await _handler.MoveToHorizontalZAsync(cancellationToken);
                await _handler.MoveToXYAsync(pickPosition, cancellationToken);
                await _handler.SetIpmGripperAsync(false, cancellationToken);
                await _handler.SetIpmLiftDownAsync(true, cancellationToken);
                await _handler.MoveAxisAsync(MotionAxis.Z, pickPosition.Z, cancellationToken);
                await _handler.SetLiftDownAsync(true, cancellationToken);
                await _handler.WaitForPcbAsync(cancellationToken);
                await _handler.SetVacuumAsync(true, cancellationToken);
                await _handler.SetIpmGripperAsync(true, cancellationToken);
                break;
            case PcbPlacementState.PlacingPcb:
            {
                var target = _handler.Pcb == PlacementPcbState.Secured
                    ? heatSink!.Value
                    : GetCurrentHeatSink(recipe)!.Value;
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                void CheckCarrier()
                {
                    if (!_work.Station.CarrierSeated || !ReferenceEquals(job, _work.CurrentJob))
                        operation.Cancel();
                }
                _work.Changed += CheckCarrier;
                try
                {
                    CheckCarrier();
                    operation.Token.ThrowIfCancellationRequested();
                    if (_handler.Pcb == PlacementPcbState.Secured)
                    {
                        var position = GetHeatSinkPosition(recipe, target);
                        await _handler.SetIpmLiftDownAsync(true, operation.Token);
                        if (!_handler.IsAtXY(position) || !_handler.IsAtZ(position))
                        {
                            await _handler.SetLiftDownAsync(false, operation.Token);
                            if (!_handler.IsAtXY(position))
                                await _handler.MoveToXYAsync(position, operation.Token);
                            await _handler.MoveAxisAsync(MotionAxis.Z, position.Z, operation.Token);
                        }
                        await _handler.SetLiftDownAsync(true, operation.Token);
                        _pressingHeatSink = null;
                        if (_repeatTrip is { } releasing)
                            releasing.Phase = RepeatPcbPhase.Releasing;
                        await _handler.SetVacuumAsync(false, operation.Token);
                    }

                    if (_pressingHeatSink != target)
                    {
                        await _handler.SetIpmGripperAsync(false, operation.Token);
                        await _handler.SetIpmLiftDownAsync(false, operation.Token);
                        _pressingHeatSink = target;
                    }
                    await _handler.SetIpmGripperAsync(true, operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    if (_pressingHeatSink != target || !_work.Station.CarrierSeated)
                        throw new InvalidOperationException("The placement carrier changed or lost seating before the PCB press.");
                    await _handler.SetIpmLiftDownAsync(true, operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    _work.GetAssembly(job, target);
                    _pressingHeatSink = null;
                    _repeatTrip = null;
                    await _handler.SetIpmLiftDownAsync(false, operation.Token);
                    await _handler.SetLiftDownAsync(false, operation.Token);
                    await _handler.MoveToHorizontalZAsync(operation.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("The placement carrier changed or lost seating during placement.");
                }
                finally
                {
                    _work.Changed -= CheckCarrier;
                }
                break;
            }
            case PcbPlacementState.CompletingCarrier:
                await _handler.SetIpmLiftDownAsync(_handler.Pcb == PlacementPcbState.Secured, cancellationToken);
                await _handler.SetLiftDownAsync(false, cancellationToken);
                await _handler.MoveToHorizontalZAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _work.Complete(job);
                break;
            default:
                return false;
        }
        return true;
    }

    public PcbPlacementState GetState(PcbPlacementRecipe recipe, bool live = true)
    {
        return GetState(recipe, TargetHeatSink, live);
    }

    public PcbPlacementState GetState(PcbPlacementRecipe recipe, HeatSinkSlot? heatSink, bool live = true)
    {
        if (!_work.Enabled)
            return PcbPlacementState.Disabled;
        var pcb = _handler.Pcb;
        var currentHeatSink = GetCurrentHeatSink(recipe, live);
        if (currentHeatSink is { } current && _work.Station.CarrierSeated && !_handler.VacuumDetected)
        {
            if (IsHeatSinkCompleted(current))
            {
                if (_handler.Lift != PlacementCylinderState.Up || !_handler.IsAtHorizontalZ(live))
                    return PcbPlacementState.PreparingPlacement;
            }
            else if ((!_repeat || _repeatTrip?.Phase is RepeatPcbPhase.Placing or RepeatPcbPhase.Releasing)
                && !_work.Completed && IsTarget(current) && pcb != PlacementPcbState.None
                && _handler.IsAtZ(GetHeatSinkPosition(recipe, current), live)
                && _handler.Lift == PlacementCylinderState.Down)
            {
                return PcbPlacementState.PlacingPcb;
            }
        }

        switch (true)
        {
            case true when _repeatTrip?.Phase == RepeatPcbPhase.Picking:
                return PcbPlacementState.PickingPcb;
            case true when _repeatTrip?.Phase == RepeatPcbPhase.ToHandoff:
                return PcbPlacementState.MovingToHandoff;
            case true when pcb == PlacementPcbState.Secured:
                switch (true)
                {
                    case true when !_repeat && _handler.IsAtBufferXY(live)
                        && _handler.IsAtHorizontalZ(live) && _handler.Lift != PlacementCylinderState.Up:
                        return _buffer.IsPlacementRaiseAllowed(live)
                            ? PcbPlacementState.PreparingPlacement
                            : PcbPlacementState.WaitingForSupplyRelease;
                    case true when _work.Station.CarrierSeated && heatSink is not null
                        && _handler.IsAtXY(GetHeatSinkPosition(recipe, heatSink.Value), live):
                        return PcbPlacementState.PlacingPcb;
                    case true when _handler.IpmLift != PlacementCylinderState.Down
                        || _handler.Lift != PlacementCylinderState.Up || !_handler.IsAtHorizontalZ(live):
                        return PcbPlacementState.PreparingPlacement;
                    case true when !_work.Station.CarrierSeated || _work.Completed:
                        return PcbPlacementState.WaitingForCarrier;
                    default:
                        return heatSink is null ? PcbPlacementState.CompletingCarrier : PcbPlacementState.PlacingPcb;
                }
            case true when _work.Station.CarrierSeated && !_work.Completed && heatSink is null:
                return PcbPlacementState.CompletingCarrier;
            case true when _repeat:
                return PcbPlacementState.WaitingForCarrier;
        }

        var supplyReady = _buffer.IsPlacementEntryAllowed(live);
        var atHandoff = _handler.IsAtBufferXY(live) && _handler.IsAtHorizontalZ(live);
        switch (true)
        {
            case true when atHandoff && _handler.Lift != PlacementCylinderState.Up
                && !supplyReady && pcb != PlacementPcbState.None:
                return PcbPlacementState.WaitingForSupply;
            case true when !atHandoff
                || (_handler.IpmGripper != PlacementGripperState.Open || _handler.IpmLift != PlacementCylinderState.Down)
                    && (!supplyReady || _handler.Lift == PlacementCylinderState.Up)
                || !supplyReady && _handler.Lift != PlacementCylinderState.Up:
                return PcbPlacementState.MovingToHandoff;
            default:
                return supplyReady ? PcbPlacementState.ReceivingPcb : PcbPlacementState.WaitingForSupply;
        }
    }

    private void OnCarrierChanged(bool _)
    {
        _pressingHeatSink = null;
        _runTargets = null;
    }

    private bool IsTarget(HeatSinkSlot heatSink)
    {
        return _runTargets?.Contains(heatSink) ?? _work.Station.IsHeatSinkPresent(heatSink);
    }

    private bool IsHeatSinkCompleted(HeatSinkSlot heatSink)
    {
        return _work.Assemblies.Any(assembly => assembly.HeatSink == heatSink);
    }

    private HeatSinkSlot? GetCurrentHeatSink(PcbPlacementRecipe recipe, bool live = true)
    {
        if (_handler.IsAtXY(recipe.HeatSink1PcbPlacementPosition, live))
        {
            return HeatSinkSlot.HeatSink1;
        }

        return _handler.IsAtXY(recipe.HeatSink2PcbPlacementPosition, live) ? HeatSinkSlot.HeatSink2 : null;
    }

    private static AxisPosition GetHeatSinkPosition(PcbPlacementRecipe recipe, HeatSinkSlot heatSink)
    {
        return heatSink == HeatSinkSlot.HeatSink1
            ? recipe.HeatSink1PcbPlacementPosition
            : recipe.HeatSink2PcbPlacementPosition;
    }
}
