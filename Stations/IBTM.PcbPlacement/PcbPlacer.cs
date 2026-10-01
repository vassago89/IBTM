using System;
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
    private readonly IIoService _io;
    private readonly PcbPlacementHandlerSettings _settings;
    private readonly IPcbSupplyHandoff _supply;
    private readonly RecipeManager _recipes;
    private readonly UnitSettings _units;
    // Original destination during this run only.
    private RepeatPcbTrip? _repeatTrip;
    private HeatSinkSlot[]? _runTargets;
    private int _targetIndex;
    // Completed stage position, invalidated by motion/state changes; never proof of current readiness.
    private AxisPosition? _handoffPosition;

    public PcbPlacer(
        IXyMotion motion,
        MotionStatus motionStatus,
        IIoService io,
        PcbPlacementHandlerSettings settings,
        IPcbSupplyHandoff supply,
        ConveyorStation station,
        RecipeManager recipes,
        UnitSettings units)
    {
        _motion = motion;
        _io = io;
        _settings = settings;
        _supply = supply;
        Station = station;
        _recipes = recipes;
        _units = units;
        Motion = motionStatus;
        Phase = PcbPlacementState.MovingToHandoff;
        io.InputChanged += OnInputChanged;
        motion.StateChanged += OnMotionStateChanged;
        station.Changed += NotifyChanged;
        station.CarrierChanged += OnCarrierChanged;
        StepChanged += NotifyChanged;
    }

    public HeatSinkSlot? ReturningPcb
    {
        get
        {
            return _units.PcbPlacement && IsRunning && _repeatTrip is { } trip
                && Phase is PcbPlacementState.ReturningToSupply
                    or PcbPlacementState.PresentingToSupply or PcbPlacementState.WaitingForSupplyGrip
                ? trip.HeatSink : null;
        }
    }

    private sealed record RepeatPcbTrip(ConveyorStation.Job Job, HeatSinkSlot HeatSink);

    public ConveyorStation Station { get; }

    private void OnMotionStateChanged()
    {
        if (_handoffPosition is { } position && !MotionServiceBase.IsHoldingPosition(_motion, position))
            _handoffPosition = null;
        NotifyChanged();
    }

    public MotionStatus Motion { get; }

    public StationCylinderState Lift
    {
        get
        {
            switch ((_io.GetInput(InputIo.PcbPlacementHandlerUp), _io.GetInput(InputIo.PcbPlacementHandlerDown)))
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
            switch ((_io.GetInput(InputIo.PcbPlacementIpmUp), _io.GetInput(InputIo.PcbPlacementIpmDown)))
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
            if (!_io.GetInput(InputIo.PcbPlacementPcbDetected))
            {
                return PlacementPcbState.None;
            }

            return _io.GetInput(InputIo.PcbPlacementVacuumDetected)
                ? PlacementPcbState.Secured
                : PlacementPcbState.Detected;
        }
    }

    public bool PcbSecured => Pcb == PlacementPcbState.Secured;

    public bool IsAtHorizontalZ
    {
        get
        {
            return MotionServiceBase.IsAtZ(_motion, _settings.HandoffPosition.Z)
                && MotionServiceBase.IsSettled(_motion, MotionAxis.Z);
        }
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.PcbPlacementHandlerDown
            or InputIo.PcbPlacementHandlerUp
            or InputIo.PcbPlacementIpmDown
            or InputIo.PcbPlacementIpmUp
            or InputIo.PcbPlacementPcbDetected
            or InputIo.PcbPlacementVacuumDetected)
        {
            NotifyChanged();
        }
    }

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
            var position = _handoffPosition;
            if (position is null || !MotionServiceBase.IsHoldingPosition(_motion, position))
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
                case PcbPlacementState.PreparingPlacement:
                case PcbPlacementState.WaitingForCarrier
                    or PcbPlacementState.PlacingPcb or PcbPlacementState.Retracting:
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

    private void OnCarrierChanged(bool present)
    {
        _runTargets = null;
        _targetIndex = 0;
    }

    private bool IsPcbGripUncertain
    {
        get
        {
            var pcb = Pcb;
            if (pcb == PlacementPcbState.Secured || !_io.GetInput(InputIo.PcbPlacementVacuumDetected))
                return false;
            if (_units.PcbPlacement
                && Phase is PcbPlacementState.ReceivingPcb or PcbPlacementState.WaitingForSupplyRelease
                && _supply.Handoff == PcbSupplyHandoff.Holding)
                return false;
            return true;
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
        if (_units.PcbPlacement && (Pcb != PlacementPcbState.None || _io.GetInput(InputIo.PcbPlacementVacuumDetected)))
            throw new InvalidOperationException("Clear the Placement PCB and vacuum before starting a new run.");
        _runTargets = null;
        _targetIndex = 0;
        _repeatTrip = null;
        _handoffPosition = null;
        Phase = repeat ? PcbPlacementState.WaitingForCarrier : PcbPlacementState.MovingToHandoff;
        try
        {
            BeginRun(_units.PcbPlacement ? Phase : PcbPlacementState.Disabled);
            _supply.Changed += WakeRun;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!Station.CarrierPresent || Station.Completed)
                {
                    _runTargets = null;
                    _targetIndex = 0;
                }
                if (_units.PcbPlacement && repeat && !_units.MainConveyor && Station.Completed
                    && Station.CarrierSeated && Phase == PcbPlacementState.WaitingForCarrier)
                    Station.StartRepeat(Station.CurrentJob);
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
            _supply.Changed -= WakeRun;
            _runTargets = null;
            _repeatTrip = null;
            _handoffPosition = null;
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
            case PcbPlacementState.PickingPcb when _repeatTrip is not null && PcbSecured:
                return PcbPlacementState.ReturningToSupply;
            case PcbPlacementState.WaitingForSupplyGrip when _supply.Handoff == PcbSupplyHandoff.Holding:
                return PcbPlacementState.ReleasingToSupply;
            case PcbPlacementState.WaitingForSupplyDeparture when _supply.Handoff == PcbSupplyHandoff.Unavailable:
                return PcbPlacementState.MovingToHandoff;
            case PcbPlacementState.WaitingForSupplyRelease when _supply.Handoff == PcbSupplyHandoff.Released:
                return PcbPlacementState.PreparingPlacement;
            case PcbPlacementState.WaitingForCarrier when Station.CarrierSeated && !Station.Completed:
                return heatSink is null ? PcbPlacementState.Retracting
                    : PcbSecured ? PcbPlacementState.PlacingPcb : PcbPlacementState.MovingToHandoff;
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
                Station.Complete(Station.CurrentJob);
            EnterStep(PcbPlacementState.Disabled, workId: Station.CurrentJob.Id,
                waitingFor: _repeatTrip is not null || PcbSecured || IsPcbGripUncertain
                    ? "unfinished PCB handoff" : Station.Completed ? "carrier transfer" : "carrier seated");
            return false;
        }
        if (Station.CarrierSeated)
            _runTargets ??= Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent).ToArray();
        var job = _repeatTrip?.Job ?? Station.CurrentJob;
        var activePcb = IsRunning ? heatSink : null;
        var targetChanged = ActivePcb != activePcb;
        ActivePcb = activePcb;
        if (state == PcbPlacementState.PreparingPlacement)
            _handoffPosition = null;
        EnterStep(state, heatSink?.ToString(), job.Id);
        if (targetChanged)
            NotifyChanged();
        if (IsPcbGripUncertain
            || state is PcbPlacementState.ReturningToSupply
                or PcbPlacementState.PresentingToSupply or PcbPlacementState.WaitingForSupplyGrip
                or PcbPlacementState.PreparingPlacement
                && !PcbSecured)
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
                    await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false, cancellationToken);
                    await SetLiftDownAsync(false, cancellationToken);
                    await MoveToXYAsync(pickPosition, cancellationToken);
                    await MoveAxisAsync(MotionAxis.Z, pickPosition.Z, cancellationToken);
                    await SetLiftDownAsync(true, cancellationToken);
                    await _io.WaitForInputAsync(InputIo.PcbPlacementPcbDetected, true, cancellationToken, requireCurrent: true);
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
                    await SetLiftDownAsync(false, cancellationToken);
                    await PrepareHandoffAsync(cancellationToken, returning: true);
                    if (!_units.PcbSupply)
                        EnterStep(PcbPlacementState.PreparingPlacement);
                    break;
                case PcbPlacementState.PresentingToSupply:
                    await PrepareReceiptAsync(cancellationToken, returning: true);
                    break;
                case PcbPlacementState.ReleasingToSupply:
                    if (_handoffPosition is not { } returnHandoff
                        || !MotionServiceBase.IsHoldingPosition(_motion, returnHandoff))
                        throw new MotionInterlockException("Placement must remain at its confirmed receive position before releasing the returned PCB.");
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
                    var departure = _motion.Position;
                    _handoffPosition = new() { X = departure.X, Y = departure.Y, Z = departure.Z };
                    EnterStep(PcbPlacementState.WaitingForSupplyDeparture);
                    break;
                case PcbPlacementState.MovingToHandoff:
                    if (_units.PcbSupply && _supply.Handoff != PcbSupplyHandoff.Holding)
                        return false;
                    await SetLiftDownAsync(false, cancellationToken);
                    await MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, cancellationToken);
                    await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, !repeat, cancellationToken);
                    await PrepareHandoffAsync(cancellationToken);
                    break;
                case PcbPlacementState.ReceivingPcb:
                {
                    if (_supply.Handoff != PcbSupplyHandoff.Holding)
                        return false;
                    using var receipt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    void CheckSupplyHolding()
                    {
                        if (_supply.Handoff != PcbSupplyHandoff.Holding && !PcbSecured)
                            OperationCancellation.CancelIfNotDisposed(receipt);
                    }
                    _supply.Changed += CheckSupplyHolding;
                    Changed += CheckSupplyHolding;
                    try
                    {
                        CheckSupplyHolding();
                        receipt.Token.ThrowIfCancellationRequested();
                        var ipmDown = !repeat;
                        if (IpmLift != (ipmDown ? StationCylinderState.Down : StationCylinderState.Up))
                            await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, ipmDown, receipt.Token);
                        await PrepareReceiptAsync(receipt.Token);
                        await _io.WaitForInputAsync(InputIo.PcbPlacementPcbDetected, true, receipt.Token, requireCurrent: true);
                        await SetVacuumAsync(true, receipt.Token);
                        receipt.Token.ThrowIfCancellationRequested();
                        if (!PcbSecured)
                            throw new InvalidOperationException("Placement receipt requires both PCB detection and vacuum before Supply releases it.");
                        EnterStep(PcbPlacementState.WaitingForSupplyRelease);
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
                    break;
                }
                case PcbPlacementState.PreparingPlacement:
                    await PreparePlacementAsync(heatSink ?? HeatSinkSlot.HeatSink1, repeat, cancellationToken);
                    // Complete this handoff before the next move invalidates its departure position.
                    NotifyChanged();
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
                        await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, !repeat, operation.Token);
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

                        await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false, operation.Token);
                        checkingPcbPresence = true;
                        CheckPlacementFeedback();
                        operation.Token.ThrowIfCancellationRequested();
                        if (!repeat)
                            await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true, operation.Token);
                        CheckPlacementFeedback();
                        operation.Token.ThrowIfCancellationRequested();
                        Station.GetAssembly(job, target);
                        _targetIndex++;
                        checkingPcbPresence = false;
                        _repeatTrip = null;
                        // Complete the rise before marking the carrier complete.
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
                    await PreparePlacementAsync(null, repeat, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var carrierComplete = TargetHeatSink is null;
                    if (carrierComplete)
                        Station.Complete(job);
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

    private async Task PreparePlacementAsync(HeatSinkSlot? departure, bool repeat, CancellationToken cancellationToken)
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
            await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, carryingPcb && !repeat, preparation.Token);
            await SetLiftDownAsync(false, preparation.Token);
            await MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, preparation.Token);
            if (carryingPcb && departure is { } destination)
                await MoveAxisAsync(MotionAxis.Y, GetHeatSinkPosition(destination).Y, preparation.Token);
            preparation.Token.ThrowIfCancellationRequested();
            var position = _motion.Position;
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
        EnterStep(returning ? PcbPlacementState.ReturningToSupply : PcbPlacementState.MovingToHandoff);
        var expected = returning ? PcbSupplyHandoff.Released : PcbSupplyHandoff.Holding;
        using var approach = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckSupply()
        {
            // Handoff confirms settled Supply XYZ and Unrotated feedback, as well as grip state.
            if (_units.PcbSupply && _supply.Handoff != expected)
                OperationCancellation.CancelIfNotDisposed(approach);
        }
        _supply.Changed += CheckSupply;
        try
        {
            CheckSupply();
            approach.Token.ThrowIfCancellationRequested();
            await MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, approach.Token);
            await MoveAxisAsync(MotionAxis.X, _settings.HandoffPosition.X, approach.Token);
            await MoveAxisAsync(MotionAxis.Y, _settings.HandoffPosition.Y, approach.Token);
            CheckSupply();
            approach.Token.ThrowIfCancellationRequested();
            _handoffPosition = new()
            {
                X = _settings.HandoffPosition.X, Y = _settings.HandoffPosition.Y, Z = _settings.HandoffPosition.Z,
            };
            EnterStep(returning ? PcbPlacementState.PresentingToSupply : PcbPlacementState.ReceivingPcb);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MotionInterlockException("Supply must remain ready at its unrotated handoff position while Placement approaches.");
        }
        finally
        {
            _supply.Changed -= CheckSupply;
        }
    }

    public async Task PrepareReceiptAsync(CancellationToken cancellationToken = default, bool returning = false)
    {
        EnterStep(returning ? PcbPlacementState.PresentingToSupply : PcbPlacementState.ReceivingPcb);
        cancellationToken.ThrowIfCancellationRequested();
        var handoffAtCurrentZ = new AxisPosition
        {
            X = _settings.HandoffPosition.X, Y = _settings.HandoffPosition.Y, Z = _motion.Position.Z,
        };
        if (!MotionServiceBase.IsHoldingPosition(_motion, handoffAtCurrentZ))
            throw new MotionInterlockException("Move Placement to handoff XY before lowering to receive Z.");
        using var receipt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckSupply()
        {
            if (returning && _units.PcbSupply && _supply.Handoff != PcbSupplyHandoff.Released)
                OperationCancellation.CancelIfNotDisposed(receipt);
        }
        _supply.Changed += CheckSupply;
        try
        {
            CheckSupply();
            receipt.Token.ThrowIfCancellationRequested();
            await MoveAxisAsync(
                MotionAxis.Z,
                _settings.ReceiveZ ?? throw new MotionInterlockException("Teach PCB Receive Z before receiving a PCB."),
                receipt.Token);
            CheckSupply();
            receipt.Token.ThrowIfCancellationRequested();
            _handoffPosition = new()
            {
                X = _settings.HandoffPosition.X, Y = _settings.HandoffPosition.Y, Z = _settings.ReceiveZ!.Value,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MotionInterlockException("Supply must remain ready at its unrotated handoff position while Placement approaches.");
        }
        finally
        {
            _supply.Changed -= CheckSupply;
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
        return _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerDown, down, cancellationToken);
    }

    public async Task SetVacuumAsync(bool on, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.PcbPlacementVacuumEjector, on);
        await _io.WaitForInputAsync(InputIo.PcbPlacementVacuumDetected, on, cancellationToken, requireCurrent: true);
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
