using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacer : AutoUnit, IPcbPlacementHandoff
{
    private readonly IXyMotion _motion;
    private readonly PcbPlacementHandlerSettings _settings;
    private readonly IPcbSupplyHandoff _supply;
    private readonly RecipeManager _recipes;
    private readonly UnitSettings _units;
    // Original destination during this run only.
    private RepeatPcbTrip? _repeatTrip;
    private HeatSinkSlot[]? _runTargets;
    private int _targetIndex;
    private long _cycleStartedAt;

    public PcbPlacer(
        IXyMotion motion,
        MotionStatus motionStatus,
        IIoService io,
        PcbPlacementHandlerSettings settings,
        IPcbSupplyHandoff supply,
        ConveyorStation station,
        RecipeManager recipes,
        UnitSettings units)
        : base(io, [
            InputIo.PcbPlacementHandlerDown,
            InputIo.PcbPlacementHandlerUp,
            InputIo.PcbPlacementIpmDown,
            InputIo.PcbPlacementIpmUp,
            InputIo.PcbPlacementPcbDetected,
            InputIo.PcbPlacementVacuumDetected,
        ])
    {
        _motion = motion;
        _settings = settings;
        _supply = supply;
        Station = station;
        IsPrefetchAllowed = true;
        _recipes = recipes;
        _units = units;
        Motion = motionStatus;
        Phase = PcbPlacementState.MovingToHandoff;
        motion.StateChanged += NotifyChanged;
        station.Changed += NotifyChanged;
        StepChanged += NotifyChanged;
    }

    public HeatSinkSlot? ReturningPcb
    {
        get
        {
            return _units.PcbPlacement && IsRunning && _repeatTrip is { } trip
                && Phase is PcbPlacementState.ReturningToSupply
                    or PcbPlacementState.WaitingForSupplyGrip
                ? trip.HeatSink : null;
        }
    }

    private sealed record RepeatPcbTrip(ConveyorStation.Job Job, HeatSinkSlot HeatSink);

    public ConveyorStation Station { get; }

    public bool IsPrefetchAllowed
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            WakeRun();
        }
    }

    public MotionStatus Motion { get; }

    public StationCylinderState Lift
    {
        get
        {
            switch ((Io.GetInput(InputIo.PcbPlacementHandlerUp), Io.GetInput(InputIo.PcbPlacementHandlerDown)))
            {
                case (true, false):
                    return StationCylinderState.Up;
                case (false, true):
                    return StationCylinderState.Down;
                default:
                    return StationCylinderState.Between;
            }
        }
    }

    public StationCylinderState IpmLift
    {
        get
        {
            switch ((Io.GetInput(InputIo.PcbPlacementIpmUp), Io.GetInput(InputIo.PcbPlacementIpmDown)))
            {
                case (true, false):
                    return StationCylinderState.Up;
                case (false, true):
                    return StationCylinderState.Down;
                default:
                    return StationCylinderState.Between;
            }
        }
    }

    public PlacementPcbState Pcb
    {
        get
        {
            if (!Io.GetInput(InputIo.PcbPlacementPcbDetected))
            {
                return PlacementPcbState.None;
            }

            return Io.GetInput(InputIo.PcbPlacementVacuumDetected)
                ? PlacementPcbState.Secured
                : PlacementPcbState.Detected;
        }
    }

    public bool PcbSecured => Pcb == PlacementPcbState.Secured;

    // Current run phase; START always begins a new handoff.
    public PcbPlacementState Phase { get; private set; }

    private void EnterStep(
        PcbPlacementState step,
        string? target = null,
        long? workId = null,
        string? waitingFor = null)
    {
        var phaseChanged = step != PcbPlacementState.Disabled && Phase != step;
        if (phaseChanged)
            Phase = step;
        base.EnterStep(step, target, workId ?? Station.CurrentJob.Id, waitingFor);
        if (phaseChanged && !IsRunning)
            NotifyChanged();
    }

    public PcbPlacementHandoff Handoff
    {
        get
        {
            if (!Io.IsReady || !MotionServiceBase.IsReadyAndStopped(_motion))
                return PcbPlacementHandoff.Unavailable;
            if (!_units.PcbPlacement
                || Lift != StationCylinderState.Up
                || IpmLift == StationCylinderState.Between)
                return PcbPlacementHandoff.Unavailable;
            switch (Phase)
            {
                case PcbPlacementState.WaitingForSupplyGrip when PcbSecured:
                    return PcbPlacementHandoff.Returning;
                case PcbPlacementState.WaitingForSupplyRelease when PcbSecured:
                    return PcbPlacementHandoff.Holding;
                case PcbPlacementState.WaitingForSupplyDeparture:
                    return PcbPlacementHandoff.Clear;
                default:
                    return PcbPlacementHandoff.Unavailable;
            }
        }
    }

    // Selected by the running sequence; bindings must not select a target from live inputs.
    public HeatSinkSlot? ActivePcb { get; private set; }

    internal HeatSinkSlot? TargetHeatSink
    {
        get
        {
            if (_repeatTrip is { } trip)
                return trip.HeatSink;
            if (Station.Completed)
                return null;
            if (_runTargets is { } targets)
                return _targetIndex < targets.Length ? targets[_targetIndex] : null;
            if (Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink1))
                return HeatSinkSlot.HeatSink1;
            return Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink2) ? HeatSinkSlot.HeatSink2 : null;
        }
    }

    private bool IsPcbGripUncertain
    {
        get
        {
            if (Pcb == PlacementPcbState.Secured || !Io.GetInput(InputIo.PcbPlacementVacuumDetected))
                return false;
            return !_units.PcbPlacement
                || Phase is not (PcbPlacementState.ReceivingPcb or PcbPlacementState.WaitingForSupplyRelease)
                || _supply.Handoff != PcbSupplyHandoff.Holding;
        }
    }

    private AxisPosition GetHeatSinkPosition(HeatSinkSlot heatSink)
    {
        var recipe = _recipes.Current.PcbPlacement;
        return heatSink == HeatSinkSlot.HeatSink1
            ? recipe.HeatSink1PcbPlacementPosition
            : recipe.HeatSink2PcbPlacementPosition;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default,
        bool repeat = false)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        if (_units.PcbPlacement && (Pcb != PlacementPcbState.None || Io.GetInput(InputIo.PcbPlacementVacuumDetected)))
            throw new InvalidOperationException("Clear the Placement PCB and vacuum before starting a new run.");
        _runTargets = null;
        _targetIndex = 0;
        _repeatTrip = null;
        Phase = _units.PcbPlacement ? PcbPlacementState.Retracting
            : repeat ? PcbPlacementState.WaitingForCarrier : PcbPlacementState.MovingToHandoff;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckCarrier()
        {
            if (_runTargets is { } targets
                && (!Station.CarrierSeated || Enum.GetValues<HeatSinkSlot>().Any(heatSink =>
                    Station.IsHeatSinkPresent(heatSink) != targets.Contains(heatSink))))
                OperationCancellation.CancelIfNotDisposed(operation);
        }
        try
        {
            Changed += CheckCarrier;
            BeginRun(_units.PcbPlacement ? Phase : PcbPlacementState.Disabled);
            _supply.Changed += WakeRun;
            if (_units.PcbPlacement)
            {
                await MoveToStandbyAsync(TargetHeatSink ?? HeatSinkSlot.HeatSink1, repeat, operation.Token);
                EnterStep(repeat ? PcbPlacementState.WaitingForCarrier : PcbPlacementState.MovingToHandoff);
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                operation.Token.ThrowIfCancellationRequested();
                if (Station.Completed)
                {
                    _runTargets = null;
                    _targetIndex = 0;
                }
                if (_units.PcbPlacement && repeat && !_units.MainConveyor && Station.Completed
                    && Station.CarrierSeated && Phase == PcbPlacementState.WaitingForCarrier)
                    Station.StartRepeat(Station.CurrentJob);
                var heatSink = TargetHeatSink;
                var step = GetNextStep(heatSink, repeat);
                if (!await ExecuteStepAsync(step, heatSink, operation.Token, repeat))
                    await WaitForChangeAsync(operation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            throw new MotionInterlockException(
                "The placement carrier changed or lost its seated / PCB presence feedback. Check the carrier before restarting.");
        }
        finally
        {
            Changed -= CheckCarrier;
            _supply.Changed -= WakeRun;
            _runTargets = null;
            _repeatTrip = null;
            ActivePcb = null;
            EndRun(cancellationToken);
        }
    }

    internal PcbPlacementState GetNextStep(HeatSinkSlot? heatSink, bool repeat = false)
    {
        if (!_units.PcbPlacement)
            return PcbPlacementState.Disabled;
        if (Station.Completed)
            return PcbPlacementState.WaitingForCarrier;
        // Completed includes live presence; recheck it after reading seating feedback below.
        var state = Phase;
        if (repeat && _repeatTrip is null)
        {
            if (state == PcbPlacementState.Retracting)
                return state;
            if (!Station.CarrierSeated || Station.Completed)
                return PcbPlacementState.WaitingForCarrier;
            return heatSink is null ? PcbPlacementState.Retracting : PcbPlacementState.PickingPcb;
        }
        switch (state)
        {
            case PcbPlacementState.WaitingForSupplyGrip when _supply.Handoff == PcbSupplyHandoff.Holding:
                return PcbPlacementState.ReleasingToSupply;
            case PcbPlacementState.WaitingForSupplyDeparture when _supply.Handoff == PcbSupplyHandoff.Unavailable:
                return PcbPlacementState.MovingToHandoff;
            case PcbPlacementState.WaitingForSupplyRelease when _supply.Handoff == PcbSupplyHandoff.Released:
                return PcbPlacementState.PreparingPlacement;
            case PcbPlacementState.WaitingForCarrier when Station.CarrierSeated && !Station.Completed:
                if (heatSink is null)
                    return PcbPlacementState.Retracting;
                return PcbSecured ? PcbPlacementState.PlacingPcb : PcbPlacementState.MovingToHandoff;
            default:
                return state;
        }
    }

    internal async Task<bool> ExecuteStepAsync(
        PcbPlacementState state,
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken,
        bool repeat = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (state == PcbPlacementState.Disabled || !_units.PcbPlacement)
        {
            ActivePcb = null;
            if (Station.CarrierSeated && _repeatTrip is null && !PcbSecured && !IsPcbGripUncertain)
                Station.Complete();
            EnterStep(PcbPlacementState.Disabled, workId: Station.CurrentJob.Id,
                waitingFor: _repeatTrip is not null || PcbSecured || IsPcbGripUncertain
                    ? "unfinished PCB handoff" : Station.Completed ? "carrier transfer" : "carrier seated");
            return false;
        }
        if (Station.CarrierSeated && !Station.Completed && _runTargets is null)
        {
            _runTargets = Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent).ToArray();
            _cycleStartedAt = Stopwatch.GetTimestamp();
        }
        var job = _repeatTrip?.Job ?? Station.CurrentJob;
        var activePcb = IsRunning ? heatSink : null;
        var targetChanged = ActivePcb != activePcb;
        ActivePcb = activePcb;
        EnterStep(state, heatSink?.ToString(), job.Id);
        if (targetChanged)
            NotifyChanged();
        var requiresSecuredPcb = state is PcbPlacementState.ReturningToSupply
            or PcbPlacementState.WaitingForSupplyGrip
            or PcbPlacementState.PreparingPlacement;
        if (IsPcbGripUncertain || requiresSecuredPcb && !PcbSecured)
            throw new InvalidOperationException("Placement PCB holding is uncertain away from a confirmed support. Check vacuum and PCB detection before moving or releasing it.");
        if (repeat && state == PcbPlacementState.PickingPcb && _repeatTrip is null)
            _repeatTrip = new(job, heatSink ?? throw new InvalidOperationException("No repeat PCB is selected."));

        var callerToken = cancellationToken;
        using var repeatOperation = _repeatTrip is null ? null
            : CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        // Placement monitors grip until descent and PCB presence during release below.
        var requiresHolding = state != PcbPlacementState.PlacingPcb && PcbSecured;
        void CheckRepeatFeedback()
        {
            if (!Station.CarrierSeated || !ReferenceEquals(job, Station.CurrentJob)
                || _repeatTrip is { } trip && !Station.IsHeatSinkPresent(trip.HeatSink)
                || requiresHolding && !PcbSecured)
                OperationCancellation.CancelIfNotDisposed(repeatOperation);
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
                    await Io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false, cancellationToken);
                    await SetLiftDownAsync(false, cancellationToken);
                    await MoveToXYAsync(pickPosition, cancellationToken);
                    await MoveAxisAsync(MotionAxis.Z, pickPosition.Z, cancellationToken);
                    await SetLiftDownAsync(true, cancellationToken);
                    await Io.WaitForInputAsync(InputIo.PcbPlacementPcbDetected, true, cancellationToken, requireCurrent: true);
                    await SetVacuumAsync(true, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!PcbSecured)
                        throw new InvalidOperationException("Repeat pickup requires both PCB detection and vacuum before raising the handler.");
                    EnterStep(PcbPlacementState.ReturningToSupply);
                    break;
                case PcbPlacementState.ReturningToSupply:
                    // Entering ReturningToSupply above requests Supply's empty handoff first.
                    if (_units.PcbSupply && _supply.Handoff != PcbSupplyHandoff.Released)
                        return false;
                    await PrepareHandoffAsync(cancellationToken, returning: true);
                    if (!_units.PcbSupply)
                        EnterStep(PcbPlacementState.PreparingPlacement);
                    break;
                case PcbPlacementState.ReleasingToSupply:
                    if (!MotionServiceBase.IsReadyAndStopped(_motion))
                        throw new MotionInterlockException("Placement axes must be ready and stopped before releasing the returned PCB.");
                    if (_supply.Handoff != PcbSupplyHandoff.Holding)
                        throw new InvalidOperationException("Supply must hold the returned PCB before placement releases it.");
                    requiresHolding = false;
                    await SetVacuumAsync(false, cancellationToken);
                    if (_supply.Handoff != PcbSupplyHandoff.Holding)
                        throw new InvalidOperationException("Supply lost the returned PCB while placement released vacuum.");
                    await SetLiftDownAsync(false, cancellationToken);
                    await MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, cancellationToken);
                    await MoveAxisAsync(MotionAxis.Y, GetHeatSinkPosition(heatSink!.Value).Y, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    EnterStep(PcbPlacementState.WaitingForSupplyDeparture);
                    break;
                case PcbPlacementState.MovingToHandoff or PcbPlacementState.ReceivingPcb:
                    // During maintenance only the seated, unfinished carrier may receive another PCB.
                    if (!IsPrefetchAllowed && (!Station.CarrierSeated || Station.Completed || heatSink is null))
                        return false;
                    if (_supply.Handoff != PcbSupplyHandoff.Holding)
                        return false;
                    await PrepareHandoffAsync(cancellationToken, repeat: repeat);
                    break;
                case PcbPlacementState.PreparingPlacement:
                    await MoveToStandbyAsync(heatSink ?? HeatSinkSlot.HeatSink1, repeat, cancellationToken);
                    // Publish Clear only after the Z/Y departure, and wait for Supply to acknowledge it.
                    EnterStep(PcbPlacementState.WaitingForSupplyDeparture);
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!PcbSecured)
                            throw new InvalidOperationException("Placement lost PCB holding while completing the supply handoff.");
                        if (_supply.Handoff == PcbSupplyHandoff.Unavailable)
                            break;
                        await WaitForChangeAsync(cancellationToken);
                    }
                    EnterStep(Station.CarrierSeated && !Station.Completed
                        ? PcbPlacementState.PlacingPcb : PcbPlacementState.WaitingForCarrier);
                    break;
                case PcbPlacementState.PlacingPcb:
                {
                    var target = heatSink ?? throw new InvalidOperationException("No placement target is selected.");
                    var position = GetHeatSinkPosition(target);
                    var carryingPcb = true;
                    if (!PcbSecured)
                        throw new InvalidOperationException("Placement requires confirmed PCB holding before travelling to its target.");
                    var checkingPcbPresence = false;
                    using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    void CheckPlacementFeedback()
                    {
                        if (!Station.CarrierSeated || !ReferenceEquals(job, Station.CurrentJob)
                            || !Station.IsHeatSinkPresent(target)
                            || carryingPcb && !PcbSecured
                            || checkingPcbPresence && Pcb == PlacementPcbState.None)
                            OperationCancellation.CancelIfNotDisposed(operation);
                    }
                    Changed += CheckPlacementFeedback;
                    try
                    {
                        CheckPlacementFeedback();
                        operation.Token.ThrowIfCancellationRequested();
                        await Io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, !repeat, operation.Token);
                        await SetLiftDownAsync(false, operation.Token);
                        await MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, operation.Token);
                        await MoveAxisAsync(MotionAxis.Y, position.Y, operation.Token);
                        await MoveAxisAsync(MotionAxis.X, position.X, operation.Token);
                        await MoveAxisAsync(MotionAxis.Z, position.Z, operation.Token);
                        await SetLiftDownAsync(true, operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        // Grip is no longer required after confirmed placement descent.
                        carryingPcb = false;
                        await SetVacuumAsync(false, operation.Token);

                        await Io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false, operation.Token);
                        checkingPcbPresence = true;
                        CheckPlacementFeedback();
                        operation.Token.ThrowIfCancellationRequested();
                        if (!repeat)
                            await Io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true, operation.Token);
                        CheckPlacementFeedback();
                        operation.Token.ThrowIfCancellationRequested();
                        Station.GetAssembly(job, target);
                        _targetIndex++;
                        checkingPcbPresence = false;
                        _repeatTrip = null;
                        // Return to standby before allowing the carrier to leave.
                        EnterStep(PcbPlacementState.Retracting);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new InvalidOperationException("Placement lost PCB grip, PCB presence during pressing, the target heat sink, or the original seated carrier.");
                    }
                    finally
                    {
                        Changed -= CheckPlacementFeedback;
                    }
                    break;
                }
                case PcbPlacementState.Retracting:
                    await MoveToStandbyAsync(null, repeat, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var carrierComplete = TargetHeatSink is null;
                    if (carrierComplete)
                    {
                        var cycleTime = _runTargets is { Length: > 0 }
                            ? Stopwatch.GetElapsedTime(_cycleStartedAt) : (TimeSpan?)null;
                        _runTargets = null;
                        _targetIndex = 0;
                        Station.Complete(job, cycleTime);
                    }
                    EnterStep(repeat && carrierComplete ? PcbPlacementState.WaitingForCarrier : PcbPlacementState.MovingToHandoff);
                    break;
                case PcbPlacementState.WaitingForSupplyRelease or PcbPlacementState.WaitingForCarrier
                    or PcbPlacementState.WaitingForSupplyGrip or PcbPlacementState.WaitingForSupplyDeparture:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported PCB placement step.");
            }
            return true;
        }
        catch (OperationCanceledException) when (repeatOperation?.IsCancellationRequested == true
            && !callerToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The repeat PCB lost its original seated carrier, target heat sink, or holding feedback.");
        }
        finally
        {
            if (repeatOperation is not null)
                Changed -= CheckRepeatFeedback;
        }
    }

    private async Task MoveToStandbyAsync(HeatSinkSlot? departure, bool repeat, CancellationToken cancellationToken)
    {
        var carryingPcb = PcbSecured;
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckHolding()
        {
            if (carryingPcb && !PcbSecured)
                OperationCancellation.CancelIfNotDisposed(preparation);
        }
        Changed += CheckHolding;
        try
        {
            CheckHolding();
            preparation.Token.ThrowIfCancellationRequested();
            await Io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, carryingPcb && !repeat, preparation.Token);
            await SetLiftDownAsync(false, preparation.Token);
            await MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, preparation.Token);
            if (departure is { } destination)
                await MoveAxisAsync(MotionAxis.Y, GetHeatSinkPosition(destination).Y, preparation.Token);
            // After placement, keep the heat-sink Y and return only X at standby Z.
            if (!carryingPcb)
                await MoveAxisAsync(MotionAxis.X, _settings.HandoffPosition.X, preparation.Token);
            preparation.Token.ThrowIfCancellationRequested();
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

    public async Task PrepareHandoffAsync(
        CancellationToken cancellationToken = default, bool returning = false, bool repeat = false)
    {
        EnterStep(returning ? PcbPlacementState.ReturningToSupply : PcbPlacementState.MovingToHandoff);
        var expected = returning ? PcbSupplyHandoff.Released : PcbSupplyHandoff.Holding;
        using var approach = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiving = false;
        void CheckSupply()
        {
            // Supply publishes handoff only after arrival, with ready axes and rotation/grip feedback.
            // Only standalone repeat may visit handoff without an enabled Supply.
            if ((!returning || _units.PcbSupply) && _supply.Handoff != expected
                && (!receiving || !PcbSecured))
                OperationCancellation.CancelIfNotDisposed(approach);
        }
        _supply.Changed += CheckSupply;
        Changed += CheckSupply;
        try
        {
            CheckSupply();
            approach.Token.ThrowIfCancellationRequested();
            await SetLiftDownAsync(false, approach.Token);
            await MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, approach.Token);
            if (!returning)
                await Io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, !repeat, approach.Token);
            await MoveAxisAsync(MotionAxis.X, _settings.HandoffPosition.X, approach.Token);
            await MoveAxisAsync(MotionAxis.Y, _settings.HandoffPosition.Y, approach.Token);
            CheckSupply();
            approach.Token.ThrowIfCancellationRequested();
            if (returning && !_units.PcbSupply)
                return;
            if (!returning)
                EnterStep(PcbPlacementState.ReceivingPcb);
            await MoveAxisAsync(
                MotionAxis.Z,
                _settings.ReceiveZ ?? throw new MotionInterlockException("Teach PCB Receive Z before receiving a PCB."),
                approach.Token);
            CheckSupply();
            approach.Token.ThrowIfCancellationRequested();
            if (!returning)
            {
                receiving = true;
                CheckSupply();
                approach.Token.ThrowIfCancellationRequested();
                await Io.WaitForInputAsync(InputIo.PcbPlacementPcbDetected, true, approach.Token, requireCurrent: true);
                await SetVacuumAsync(true, approach.Token);
                approach.Token.ThrowIfCancellationRequested();
                if (!PcbSecured)
                    throw new InvalidOperationException("Placement receipt requires both PCB detection and vacuum before Supply releases it.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (receiving)
                throw new InvalidOperationException("Supply lost confirmed PCB holding during receipt.");
            throw new MotionInterlockException("Supply must remain ready at its unrotated handoff position while Placement approaches.");
        }
        finally
        {
            _supply.Changed -= CheckSupply;
            Changed -= CheckSupply;
        }
        // Publishing Returning permits Supply to grip; it no longer has to remain Released.
        if (PcbSecured)
            EnterStep(returning ? PcbPlacementState.WaitingForSupplyGrip : PcbPlacementState.WaitingForSupplyRelease);
    }

    public async Task<bool> HomeAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        return await _motion.HomeAsync(axis, _settings.Motion.Home(axis).SearchSpeed, cancellationToken);
    }

    public async Task<bool> HomeHorizontalAsync(CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        return await _motion.HomeHorizontalAsync(
            _settings.Motion.HorizontalHome.SearchSpeed,
            cancellationToken);
    }

    public Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        var speed = axis == MotionAxis.Z ? _settings.Motion.ZSpeed : _settings.Motion.HorizontalSpeed;
        if (axis == MotionAxis.Z && !_motion.GetAxisState(axis).Homed)
            throw new MotionInterlockException("Home Placement Z before moving to a taught height.");
        return _motion.MoveAxisAsync(axis, position, speed, cancellationToken);
    }

    public async Task MoveToXYAsync(AxisPosition position, CancellationToken cancellationToken = default)
    {
        await MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, cancellationToken);
        EnsureHandlerRaised(cancellationToken);
        await _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    public async Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default)
    {
        switch (point.Mode)
        {
            case TeachMode.ZOnly:
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                break;
            case TeachMode.XYOnly:
                await MoveToXYAsync(position, cancellationToken);
                break;
            case TeachMode.Full when point.Target == TeachingTarget.PlacementHandoff:
                EnsureHandlerRaised(cancellationToken);
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                await MoveAxisAsync(MotionAxis.X, position.X, cancellationToken);
                await MoveAxisAsync(MotionAxis.Y, position.Y, cancellationToken);
                break;
            case TeachMode.Full when point.Target is TeachingTarget.HeatSink1PcbPlacement
                or TeachingTarget.HeatSink2PcbPlacement:
                await MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, cancellationToken);
                await MoveAxisAsync(MotionAxis.Y, position.Y, cancellationToken);
                await MoveAxisAsync(MotionAxis.X, position.X, cancellationToken);
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                break;
            case TeachMode.Full:
                if (!double.IsFinite(position.Z))
                    throw new ArgumentOutOfRangeException(nameof(position), "Target Z must be finite before XY movement.");
                await MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, cancellationToken);
                EnsureHandlerRaised(cancellationToken);
                await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, cancellationToken);
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    public Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (axis != MotionAxis.Z)
            EnsureHandlerRaised(cancellationToken);
        return _motion.JogAsync(axis, velocity, cancellationToken);
    }

    public Task AdjustAxisAsync(
        MotionAxis axis, double position, double velocity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (axis != MotionAxis.Z)
            EnsureHandlerRaised(cancellationToken);
        return _motion.AdjustAxisAsync(axis, position, velocity, cancellationToken);
    }

    public Task SetLiftDownAsync(bool down, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (down && _motion.IsMoving)
            throw new MotionInterlockException("Stop the placement axes before lowering the handler.");
        return Io.SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerDown, down, cancellationToken);
    }

    public async Task SetVacuumAsync(bool on, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Io.SetOutput(OutputIo.PcbPlacementVacuumEjector, on);
        try
        {
            await Io.WaitForInputAsync(InputIo.PcbPlacementVacuumDetected, on, cancellationToken, requireCurrent: true);
        }
        catch (IoTimeoutException exception) when (on && IsRunning
            && !cancellationToken.IsCancellationRequested
            && Io.GetInput(InputIo.PcbPlacementPcbDetected)
            && MotionServiceBase.IsReadyAndStopped(_motion)
            && (Phase == PcbPlacementState.ReceivingPcb
                    && _supply.Handoff == PcbSupplyHandoff.Holding
                    && Lift == StationCylinderState.Up
                || Phase == PcbPlacementState.PickingPcb && _repeatTrip is { } trip
                    && Station.CarrierSeated && Station.IsHeatSinkPresent(trip.HeatSink)
                    && Lift == StationCylinderState.Down))
        {
            throw new MaintenanceStopException("Placement vacuum was not detected before lifting. The PCB remains on its support; check the vacuum and remove the PCB before restarting.", exception);
        }
    }

    private void EnsureHandlerRaised(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Lift != StationCylinderState.Up)
        {
            throw new MotionInterlockException("Raise the placement handler before moving any axis.");
        }
    }
}
