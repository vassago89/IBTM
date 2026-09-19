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

    private Task ExecuteAsync(PcbPlacementRecipe recipe, CancellationToken cancellationToken)
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
            return ExecuteRepeatAsync(recipe, heatSink, cancellationToken);
        var action = PlaceStepAsync(recipe, heatSink, cancellationToken);
        return action ?? WaitForChangeAsync(cancellationToken);
    }

    // Execute one production action for the requested heat sink; null means waiting.
    public Task? PlaceStepAsync(
        PcbPlacementRecipe recipe,
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = _work.CurrentJob;
        var state = GetState(recipe, heatSink);
        TraceStep(
            state,
            _repeatTrip is { } trip ? $"{trip.HeatSink}, Repeat {trip.Phase}" : heatSink?.ToString(),
            job.Id);
        switch (state)
        {
            case PcbPlacementState.RaisingHandler:
                return _handler.SetLiftDownAsync(false, cancellationToken);
            case PcbPlacementState.RaisingZ:
                return _handler.MoveToHorizontalZAsync(cancellationToken);
            case PcbPlacementState.UnrotatingForBuffer:
                return _handler.SetRotatedAsync(false, cancellationToken);
            case PcbPlacementState.OpeningGripper:
                return _handler.SetIpmGripperAsync(false, cancellationToken);
            case PcbPlacementState.LoweringIpm:
                return _handler.SetIpmLiftDownAsync(true, cancellationToken);
            case PcbPlacementState.PressingPcb:
                return PressPcbAsync(GetCurrentHeatSink(recipe)!.Value, cancellationToken);
            case PcbPlacementState.MovingAboveBuffer:
                return _handler.MoveAboveBufferAsync(cancellationToken);
            case PcbPlacementState.LoweringHandler:
                return _handler.SetLiftDownAsync(true, cancellationToken);
            case PcbPlacementState.WaitingForPcbDetection:
                return _handler.WaitForPcbAsync(cancellationToken);
            case PcbPlacementState.ApplyingVacuum:
                return _handler.SetVacuumAsync(true, cancellationToken);
            case PcbPlacementState.ClosingGripper:
                return _handler.SetIpmGripperAsync(true, cancellationToken);
            case PcbPlacementState.RaisingIpm:
                return _handler.SetIpmLiftDownAsync(false, cancellationToken);
            case PcbPlacementState.RotatingForPlacement:
                return _handler.SetRotatedAsync(true, cancellationToken);
            case PcbPlacementState.MovingAboveHeatSink:
                return _handler.MoveToXYAsync(
                    GetHeatSinkPosition(recipe, heatSink!.Value),
                    cancellationToken);
            case PcbPlacementState.LoweringToHeatSink:
                return _handler.MoveAxisAsync(
                    MotionAxis.Z,
                    GetHeatSinkPosition(recipe, heatSink!.Value).Z,
                    cancellationToken);
            case PcbPlacementState.ReleasingVacuum:
                _pressingHeatSink = null;
                if (_repeatTrip is { } releasing)
                    releasing.Phase = RepeatPcbPhase.Releasing;
                return _handler.SetVacuumAsync(false, cancellationToken);
            case PcbPlacementState.RecordingPlacement:
                _work.GetAssembly(job, GetCurrentHeatSink(recipe)!.Value);
                _pressingHeatSink = null;
                _repeatTrip = null;
                break;
            case PcbPlacementState.CompletingCarrier:
                _work.Complete(job);
                break;
            default:
                return null;
        }

        return Task.CompletedTask;
    }

    private async Task PressPcbAsync(HeatSinkSlot heatSink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pressingHeatSink = heatSink;
        await _handler.SetIpmGripperAsync(true, cancellationToken);
        if (_pressingHeatSink != heatSink || !_work.Station.CarrierSeated)
            throw new InvalidOperationException("The placement carrier changed or lost seating before the PCB press.");
        await _handler.SetIpmLiftDownAsync(true, cancellationToken);
    }

    public PcbPlacementState GetState(PcbPlacementRecipe recipe, bool live = true)
    {
        return GetState(recipe, TargetHeatSink, live);
    }

    public PcbPlacementState GetState(PcbPlacementRecipe recipe, HeatSinkSlot? heatSink, bool live = true)
    {
        var pcb = _handler.Pcb;
        var currentHeatSink = GetCurrentHeatSink(recipe, live);
        if (currentHeatSink is not null
            && _work.Station.CarrierSeated
            && !_handler.VacuumDetected
            && (IsHeatSinkCompleted(currentHeatSink.Value)
                || ((!_repeat || _repeatTrip?.Phase is RepeatPcbPhase.Placing or RepeatPcbPhase.Releasing)
                    && !_work.Completed
                    && IsTarget(currentHeatSink.Value)
                    && pcb != PlacementPcbState.None
                    && _handler.Rotation == PlacementRotationState.Rotated
                    && _handler.IsAtZ(GetHeatSinkPosition(recipe, currentHeatSink.Value), live)
                    && _handler.Lift == PlacementCylinderState.Down)))
        {
            var state = GetFinishPlacementState(currentHeatSink.Value, live);
            if (state is not null)
            {
                return state.Value;
            }
        }

        switch (true)
        {
            case true when _repeatTrip is { Phase: RepeatPcbPhase.Picking or RepeatPcbPhase.ToHandoff } trip:
                return GetRepeatPickupState(recipe, trip, live);
            case true when pcb == PlacementPcbState.Secured:
                switch (true)
                {
                    case true when !_repeat && _handler.IsAtBufferXY(live)
                        && _handler.IsAtHorizontalZ(live)
                        && _handler.Lift != PlacementCylinderState.Up:
                        return _buffer.IsPlacementRaiseAllowed(live)
                            ? PcbPlacementState.RaisingHandler
                            : PcbPlacementState.WaitingForSupplyRelease;
                    case true when _handler.IpmLift != PlacementCylinderState.Down:
                        return PcbPlacementState.LoweringIpm;
                    case true when _work.Station.CarrierSeated
                        && heatSink is not null
                        && _handler.Rotation == PlacementRotationState.Rotated
                        && _handler.IsAtXY(GetHeatSinkPosition(recipe, heatSink.Value), live):
                        return GetPlacementState(GetHeatSinkPosition(recipe, heatSink.Value), live);
                    case true when _handler.Lift != PlacementCylinderState.Up:
                        return PcbPlacementState.RaisingHandler;
                    case true when !_handler.IsAtHorizontalZ(live):
                        return PcbPlacementState.RaisingZ;
                    case true when !_work.Station.CarrierSeated || _work.Completed:
                        return PcbPlacementState.WaitingForCarrier;
                    default:
                        return heatSink is null
                            ? PcbPlacementState.CompletingCarrier
                            : GetPlacementState(GetHeatSinkPosition(recipe, heatSink.Value), live);
                }
            case true when _work.Station.CarrierSeated && !_work.Completed && heatSink is null:
                switch (true)
                {
                    case true when _handler.IpmLift != PlacementCylinderState.Up:
                        return PcbPlacementState.RaisingIpm;
                    case true when _handler.Lift != PlacementCylinderState.Up:
                        return PcbPlacementState.RaisingHandler;
                    case true when !_handler.IsAtHorizontalZ(live):
                        return PcbPlacementState.RaisingZ;
                    default:
                        return PcbPlacementState.CompletingCarrier;
                }
            default:
                return _repeat ? PcbPlacementState.WaitingForCarrier : GetHandoffPickupState(live);
        }
    }

    private PcbPlacementState GetHandoffPickupState(bool live = true)
    {
        // Both handlers approach independently with the receiving cylinder Up.
        // Only cylinder descent waits for Supply to be settled and holding its PCB.
        var supplyReady = _buffer.IsPlacementEntryAllowed(live);
        var atBuffer = _handler.IsAtBufferXY(live);
        var atBufferZ = _handler.IsAtHorizontalZ(live);
        var rotation = _handler.Rotation;
        switch (true)
        {
            case true when atBuffer && atBufferZ
                && _handler.Lift != PlacementCylinderState.Up
                && !supplyReady && _handler.Pcb != PlacementPcbState.None:
                // Do not lift away from an interrupted receipt with uncertain holding feedback.
                return PcbPlacementState.WaitingForSupply;
            case true when _handler.Lift != PlacementCylinderState.Up
                && (!supplyReady || !atBuffer || !atBufferZ || rotation != PlacementRotationState.Unrotated):
                return PcbPlacementState.RaisingHandler;
            case true when !atBufferZ:
                return PcbPlacementState.RaisingZ;
            case true when rotation != PlacementRotationState.Unrotated:
                return PcbPlacementState.UnrotatingForBuffer;
            case true when _handler.IpmGripper != PlacementGripperState.Open:
                return PcbPlacementState.OpeningGripper;
            case true when _handler.IpmLift != PlacementCylinderState.Down:
                return PcbPlacementState.LoweringIpm;
            case true when !atBuffer:
                return PcbPlacementState.MovingAboveBuffer;
            case true when !supplyReady:
                return PcbPlacementState.WaitingForSupply;
            case true when _handler.Lift != PlacementCylinderState.Down:
                return PcbPlacementState.LoweringHandler;
            case true when _handler.Pcb == PlacementPcbState.None:
                return PcbPlacementState.WaitingForPcbDetection;
            case true when !_handler.VacuumDetected:
                return PcbPlacementState.ApplyingVacuum;
            default:
                return PcbPlacementState.ClosingGripper;
        }
    }

    private PcbPlacementState GetPlacementState(AxisPosition position, bool live = true)
    {
        switch (true)
        {
            case true when _handler.Lift != PlacementCylinderState.Up
                && (!_handler.IsAtXY(position, live) || !_handler.IsAtZ(position, live)):
                return PcbPlacementState.RaisingHandler;
            case true when !_handler.IsAtXY(position, live):
                return PcbPlacementState.MovingAboveHeatSink;
            case true when _handler.Rotation != PlacementRotationState.Rotated:
                return !_handler.IsAtHorizontalZ(live)
                    ? PcbPlacementState.RaisingZ
                    : PcbPlacementState.RotatingForPlacement;
            case true when !_handler.IsAtZ(position, live):
                return PcbPlacementState.LoweringToHeatSink;
            case true when _handler.Lift != PlacementCylinderState.Down:
                return PcbPlacementState.LoweringHandler;
            case true when _handler.VacuumDetected:
                return PcbPlacementState.ReleasingVacuum;
            case true when _handler.IpmGripper != PlacementGripperState.Open:
                return PcbPlacementState.OpeningGripper;
            default:
                return PcbPlacementState.WaitingForSupply;
        }
    }

    private PcbPlacementState? GetFinishPlacementState(HeatSinkSlot heatSink, bool live = true)
    {
        var ipm = _handler.IpmLift;
        switch (true)
        {
            case true when IsHeatSinkCompleted(heatSink):
                switch (true)
                {
                    case true when _handler.Lift == PlacementCylinderState.Up && _handler.IsAtHorizontalZ(live):
                        return null;
                    case true when ipm != PlacementCylinderState.Up:
                        return PcbPlacementState.RaisingIpm;
                    case true when _handler.Lift != PlacementCylinderState.Up:
                        return PcbPlacementState.RaisingHandler;
                    default:
                        return PcbPlacementState.RaisingZ;
                }
            case true when _pressingHeatSink == heatSink:
                return ipm == PlacementCylinderState.Down
                    && _handler.IpmGripper == PlacementGripperState.Closed
                    ? PcbPlacementState.RecordingPlacement
                    : PcbPlacementState.PressingPcb;
            case true when _handler.IpmGripper != PlacementGripperState.Open:
                return PcbPlacementState.OpeningGripper;
            case true when ipm != PlacementCylinderState.Up:
                return PcbPlacementState.RaisingIpm;
            default:
                return PcbPlacementState.PressingPcb;
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
