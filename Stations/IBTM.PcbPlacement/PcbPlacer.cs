using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.PcbPlacement;

public sealed partial class PcbPlacer : AutoUnit, IPcbPlacementHandoff
{
    private readonly IPcbSupplyHandoff _supply;
    private readonly RecipeManager _recipes;
    private readonly PcbPlacementHandler _handler;
    private readonly PcbPlacementWork _work;
    private HeatSinkSlot[]? _runTargets;
    // Down before release and Down after pressing have identical IO feedback.
    // Keep the press target only while this run owns the operation.
    private HeatSinkSlot? _pressingHeatSink;

    public PcbPlacer(IPcbSupplyHandoff supply, PcbPlacementHandler handler, PcbPlacementWork work, RecipeManager recipes)
    {
        _supply = supply;
        _recipes = recipes;
        _handler = handler;
        _work = work;
        _work.Station.CarrierChanged += OnCarrierChanged;
    }

    public override event Action? Changed
    {
        add
        {
            _handler.Feedback.StateChanged += value;
            _handler.Changed += value;
            _work.Changed += value;
        }

        remove
        {
            _handler.Feedback.StateChanged -= value;
            _handler.Changed -= value;
            _work.Changed -= value;
        }
    }

    public PcbPlacementState State => GetState();

    public PcbPlacementHandoff Handoff
    {
        get
        {
            switch (State)
            {
                case PcbPlacementState.WaitingForSupplyRelease:
                    return PcbPlacementHandoff.Holding;
                case PcbPlacementState.WaitingForSupply or PcbPlacementState.WaitingForCarrier
                    or PcbPlacementState.PlacingPcb or PcbPlacementState.CompletingCarrier:
                    return PcbPlacementHandoff.Clear;
                default:
                    return PcbPlacementHandoff.Unavailable;
            }
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
        CancellationToken cancellationToken = default,
        bool repeat = false)
    {
        _repeat = repeat;
        _runTargets = null;
        try
        {
            BeginRun();
            _supply.Changed += OnChanged;
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
                        await ExecuteRepeatAsync(heatSink, cancellationToken);
                    else if (!await PlaceAsync(heatSink, cancellationToken))
                        await WaitForChangeAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                _supply.Changed -= OnChanged;
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

    // A state owns the whole operation. False means an external handoff/carrier wait.
    public async Task<bool> PlaceAsync(
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = _work.CurrentJob;
        var state = GetState(heatSink);
        switch (state)
        {
            case PcbPlacementState.WaitingForSupply when _supply.Handoff == PcbSupplyHandoff.Holding:
                state = PcbPlacementState.ReceivingPcb;
                break;
            case PcbPlacementState.WaitingForSupplyRelease when _supply.Handoff == PcbSupplyHandoff.Released:
                state = PcbPlacementState.PreparingPlacement;
                break;
        }
        TraceStep(state, _repeatTrip is { } trip ? $"{trip.HeatSink}, Repeat {trip.Phase}" : heatSink?.ToString(), job.Id);
        switch (state)
        {
            case PcbPlacementState.MovingToHandoff:
                await _handler.SetLiftDownAsync(false, cancellationToken);
                await _handler.MoveToHorizontalZAsync(cancellationToken);
                if (_repeatTrip?.Phase != RepeatPcbPhase.ToHandoff)
                    await _handler.SetIpmGripperAsync(false, cancellationToken);
                await _handler.SetIpmLiftDownAsync(true, cancellationToken);
                await _handler.MoveToHandoffXYAsync(cancellationToken);
                break;
            case PcbPlacementState.ReceivingPcb:
                if (_supply.Handoff != PcbSupplyHandoff.Holding)
                    return false;
                if (_handler.IpmGripper != PlacementGripperState.Open)
                    await _handler.SetIpmGripperAsync(false, cancellationToken);
                if (_handler.IpmLift != PlacementCylinderState.Down)
                    await _handler.SetIpmLiftDownAsync(true, cancellationToken);
                if (_supply.Handoff != PcbSupplyHandoff.Holding)
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
                var pickPosition = GetHeatSinkPosition(heatSink!.Value);
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
                    : GetCurrentHeatSink()!.Value;
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
                        var position = GetHeatSinkPosition(target);
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

    public PcbPlacementState GetState(bool live = true)
    {
        return GetState(TargetHeatSink, live);
    }

    public PcbPlacementState GetState(HeatSinkSlot? heatSink, bool live = true)
    {
        if (!_work.Enabled)
            return PcbPlacementState.Disabled;
        var pcb = _handler.Pcb;
        var currentHeatSink = GetCurrentHeatSink(live);
        if (currentHeatSink is { } current && _work.Station.CarrierSeated && !_handler.VacuumDetected)
        {
            if (IsHeatSinkCompleted(current))
            {
                if (_handler.Lift != PlacementCylinderState.Up || !_handler.IsAtHorizontalZ(live))
                    return PcbPlacementState.PreparingPlacement;
            }
            else if ((!_repeat || _repeatTrip?.Phase is RepeatPcbPhase.Placing or RepeatPcbPhase.Releasing)
                && !_work.Completed && IsTarget(current) && pcb != PlacementPcbState.None
                && _handler.IsAtZ(GetHeatSinkPosition(current), live)
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
                    case true when !_repeat && _handler.IsAtHandoffXY(live)
                        && _handler.IsAtHorizontalZ(live) && _handler.Lift != PlacementCylinderState.Up:
                        return _handler.Lift == PlacementCylinderState.Down && _handler.IsAtHandoff(live)
                            ? PcbPlacementState.WaitingForSupplyRelease
                            : PcbPlacementState.ReceivingPcb;
                    case true when _work.Station.CarrierSeated && heatSink is not null
                        && _handler.IsAtXY(GetHeatSinkPosition(heatSink.Value), live):
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
                return _handler.Lift == PlacementCylinderState.Up && _handler.IsAtHorizontalZ(live)
                    ? PcbPlacementState.CompletingCarrier
                    : PcbPlacementState.PreparingPlacement;
            case true when _repeat:
                return PcbPlacementState.WaitingForCarrier;
        }

        var atHandoff = _handler.IsAtHandoff(live);
        switch (true)
        {
            case true when !atHandoff:
                return PcbPlacementState.MovingToHandoff;
            case true when _handler.Lift != PlacementCylinderState.Up:
                return PcbPlacementState.ReceivingPcb;
            case true when _handler.IpmGripper != PlacementGripperState.Open || _handler.IpmLift != PlacementCylinderState.Down:
                return PcbPlacementState.MovingToHandoff;
            default:
                return PcbPlacementState.WaitingForSupply;
        }
    }

    private void OnCarrierChanged(bool present)
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

    private HeatSinkSlot? GetCurrentHeatSink(bool live = true)
    {
        var recipe = _recipes.Current.PcbPlacement;
        if (_handler.IsAtXY(recipe.HeatSink1PcbPlacementPosition, live))
        {
            return HeatSinkSlot.HeatSink1;
        }

        return _handler.IsAtXY(recipe.HeatSink2PcbPlacementPosition, live) ? HeatSinkSlot.HeatSink2 : null;
    }

    private AxisPosition GetHeatSinkPosition(HeatSinkSlot heatSink)
    {
        var recipe = _recipes.Current.PcbPlacement;
        return heatSink == HeatSinkSlot.HeatSink1
            ? recipe.HeatSink1PcbPlacementPosition
            : recipe.HeatSink2PcbPlacementPosition;
    }
}
