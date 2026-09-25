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
    // Ownership until the PCB is placed back on its original carrier, including STOP.
    private RepeatPcbTrip? _repeatTrip;
    private HeatSinkSlot[]? _runTargets;
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

    public HeatSinkSlot? ReturningPcb => _units.PcbPlacement && IsRunning && _repeatTrip is { } trip
        && Phase is PcbPlacementState.ReturningToSupply or PcbPlacementState.WaitingForSupplyReceipt
            or PcbPlacementState.PresentingToSupply or PcbPlacementState.WaitingForSupplyGrip
        ? trip.HeatSink : null;

    private sealed record RepeatPcbTrip(ConveyorStation.Job Job, HeatSinkSlot HeatSink);

    public ConveyorStation Station { get; }

    public override event Action? Changed;

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private void OnMotionStateChanged()
    {
        _handoffPosition = null;
        NotifyChanged();
    }

    public MotionStatus Motion { get; }

    public PlacementCylinderState Lift
    {
        get
        {
            switch ((_io.GetInput(InputIo.PcbPlacementHandlerUp), _io.GetInput(InputIo.PcbPlacementHandlerDown)))
            {
                case (true, false):
                    return PlacementCylinderState.Up;
                case (false, true):
                    return PlacementCylinderState.Down;
                default:
                    return PlacementCylinderState.Between;
            }
        }
    }

    public bool HandlerRaised => Lift == PlacementCylinderState.Up;

    public PlacementCylinderState IpmLift
    {
        get
        {
            switch ((_io.GetInput(InputIo.PcbPlacementIpmUp), _io.GetInput(InputIo.PcbPlacementIpmDown)))
            {
                case (true, false):
                    return PlacementCylinderState.Up;
                case (false, true):
                    return PlacementCylinderState.Down;
                default:
                    return PlacementCylinderState.Between;
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
            return Motion.IsAtZ(_settings.HandoffPosition.Z, live: true)
                && Motion.IsSettled(live: true, MotionAxis.Z);
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
            Changed?.Invoke();
        }
    }

    // Retained handoff phase; it is not proof of physical position or an active run.
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
            Changed?.Invoke();
    }

    public PcbPlacementHandoff Handoff
    {
        get
        {
            var position = _handoffPosition;
            if (position is null || !Motion.IsHoldingPosition(position))
                return PcbPlacementHandoff.Unavailable;
            if (!_units.PcbPlacement || !HandlerRaised || IpmLift == PlacementCylinderState.Between)
                return PcbPlacementHandoff.Unavailable;
            switch (Phase)
            {
                case PcbPlacementState.WaitingForSupplyGrip when PcbSecured:
                    return PcbPlacementHandoff.Returning;
                case PcbPlacementState.WaitingForSupplyRelease when PcbSecured:
                    return PcbPlacementHandoff.Holding;
                case PcbPlacementState.WaitingForSupply when !PcbSecured:
                case PcbPlacementState.WaitingForSupplyDeparture:
                case PcbPlacementState.WaitingForSupplyClear:
                case PcbPlacementState.WaitingForCarrier
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
                case true when Station.Completed:
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

    private void OnCarrierChanged(bool present)
    {
        _runTargets = null;
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

    private bool IsTarget(HeatSinkSlot heatSink)
    {
        return _runTargets?.Contains(heatSink) ?? Station.IsHeatSinkPresent(heatSink);
    }

    private bool IsHeatSinkCompleted(HeatSinkSlot heatSink)
    {
        return Station.Assemblies.Any(assembly => assembly.HeatSink == heatSink);
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
        // Reject a mode change before publishing the retained handoff to Supply.
        if (_units.PcbPlacement && !repeat && _repeatTrip is not null && !cancellationToken.IsCancellationRequested)
            throw new InvalidOperationException("An unfinished Repeat PCB must be returned to its original carrier in Repeat mode before normal operation.");
        _runTargets = null;
        try
        {
            BeginRun(_units.PcbPlacement ? Phase : PcbPlacementState.Disabled);
            _supply.Changed += OnChanged;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_units.PcbPlacement && _handoffPosition is { } handoff && !Motion.IsHoldingPosition(handoff))
                {
                    _handoffPosition = null;
                    NotifyChanged();
                }
                if (!Station.CarrierPresent || Station.Completed)
                {
                    _runTargets = null;
                }
                if (_units.PcbPlacement && repeat && !_units.MainConveyor && Station.Completed
                    && Station.CarrierSeated && Phase == PcbPlacementState.WaitingForCarrier)
                    Station.StartRepeat(Station.CurrentJob);
                if (Station.CarrierSeated)
                {
                    _runTargets ??= Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent).ToArray();
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
            EndRun(cancellationToken);
        }
    }

    public PcbPlacementState GetNextStep(HeatSinkSlot? heatSink, bool repeat = false)
    {
        if (!_units.PcbPlacement)
            return PcbPlacementState.Disabled;
        var state = Phase;
        if (repeat && _repeatTrip is null)
        {
            if (state == PcbPlacementState.CompletingCarrier)
                return state;
            if (!Station.CarrierSeated || Station.Completed)
                return PcbPlacementState.WaitingForCarrier;
            return heatSink is null ? PcbPlacementState.CompletingCarrier : PcbPlacementState.PickingPcb;
        }
        switch (state)
        {
            case PcbPlacementState.PickingPcb when _repeatTrip is not null && PcbSecured:
                return PcbPlacementState.ReturningToSupply;
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
            case PcbPlacementState.WaitingForSupplyClear when _supply.Handoff == PcbSupplyHandoff.Unavailable:
                return Station.CarrierSeated && !Station.Completed
                    ? PcbPlacementState.PlacingPcb : PcbPlacementState.WaitingForCarrier;
            case PcbPlacementState.WaitingForCarrier when Station.CarrierSeated && !Station.Completed:
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
        if (state == PcbPlacementState.Disabled || !_units.PcbPlacement)
        {
            if (Station.CarrierSeated && _repeatTrip is null && !PcbSecured && !IsPcbGripUncertain)
                Station.Complete(Station.CurrentJob);
            EnterStep(PcbPlacementState.Disabled, workId: Station.CurrentJob.Id,
                waitingFor: _repeatTrip is not null || PcbSecured || IsPcbGripUncertain
                    ? "unfinished PCB handoff" : Station.Completed ? "carrier transfer" : "carrier seated");
            return false;
        }
        if (!repeat && _repeatTrip is not null)
            throw new InvalidOperationException("An unfinished Repeat PCB must be returned to its original carrier in Repeat mode before normal operation.");
        var job = _repeatTrip?.Job ?? Station.CurrentJob;
        EnterStep(state, heatSink?.ToString(), job.Id);
        if (IsPcbGripUncertain
            || state is PcbPlacementState.ReturningToSupply or PcbPlacementState.WaitingForSupplyReceipt
                or PcbPlacementState.PresentingToSupply or PcbPlacementState.WaitingForSupplyGrip
                or PcbPlacementState.WaitingForSupplyClear
                && !PcbSecured
            || _repeatTrip is not null && state == PcbPlacementState.PreparingPlacement && !PcbSecured)
            throw new InvalidOperationException("Placement PCB holding is uncertain away from a confirmed support. Check vacuum and PCB detection before moving or releasing it.");
        if (repeat && state == PcbPlacementState.PickingPcb && _repeatTrip is null)
            _repeatTrip = new(job, heatSink ?? throw new InvalidOperationException("No repeat PCB is selected."));

        var callerToken = cancellationToken;
        using var repeatOperation = _repeatTrip is null ? null
            : CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        var requiresHolding = PcbSecured;
        void CheckRepeatFeedback()
        {
            if (!Station.CarrierSeated || !ReferenceEquals(job, Station.CurrentJob)
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
                    await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false, cancellationToken);
                    await SetLiftDownAsync(false, cancellationToken);
                    await MoveToXYAsync(pickPosition, cancellationToken);
                    await MoveAxisAsync(MotionAxis.Z, pickPosition.Z, cancellationToken);
                    await SetLiftDownAsync(true, cancellationToken);
                    await WaitForPcbAsync(cancellationToken);
                    await SetVacuumAsync(true, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!PcbSecured)
                        throw new InvalidOperationException("Repeat pickup requires both PCB detection and vacuum before raising the handler.");
                    EnterStep(PcbPlacementState.ReturningToSupply);
                    break;
                case PcbPlacementState.ReturningToSupply:
                    if (!PcbSecured)
                        throw new InvalidOperationException("Returning a PCB requires confirmed holding feedback.");
                    await SetLiftDownAsync(false, cancellationToken);
                    await PrepareHandoffAsync(cancellationToken, returning: true);
                    if (!_units.PcbSupply)
                        EnterStep(PcbPlacementState.PreparingPlacement);
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
                    var departure = _motion.Position;
                    _handoffPosition = new() { X = departure.X, Y = departure.Y, Z = departure.Z };
                    EnterStep(PcbPlacementState.WaitingForSupplyDeparture);
                    break;
                case PcbPlacementState.MovingToHandoff:
                    await SetLiftDownAsync(false, cancellationToken);
                    await MoveToHorizontalZAsync(cancellationToken);
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
                            receipt.Cancel();
                    }
                    _supply.Changed += CheckSupplyHolding;
                    Changed += CheckSupplyHolding;
                    try
                    {
                        CheckSupplyHolding();
                        receipt.Token.ThrowIfCancellationRequested();
                        var ipmDown = !repeat;
                        if (IpmLift != (ipmDown ? PlacementCylinderState.Down : PlacementCylinderState.Up))
                            await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, ipmDown, receipt.Token);
                        await PrepareReceiptAsync(receipt.Token);
                        await WaitForPcbAsync(receipt.Token);
                        await SetVacuumAsync(true, receipt.Token);
                        receipt.Token.ThrowIfCancellationRequested();
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
                    if (PcbSecured)
                        // Keep the confirmed departure position until Supply observes Clear.
                        // Starting placement immediately can erase Clear before its loop wakes.
                        EnterStep(PcbPlacementState.WaitingForSupplyClear);
                    else
                    {
                        EnterStep(TargetHeatSink is null && Station.CarrierSeated && !Station.Completed
                            ? PcbPlacementState.CompletingCarrier : PcbPlacementState.MovingToHandoff);
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
                        if (!Station.CarrierSeated || !ReferenceEquals(job, Station.CurrentJob)
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
                        await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, !repeat, operation.Token);
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

                        await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false, operation.Token);
                        checkingPcbPresence = true;
                        CheckPlacementFeedback();
                        operation.Token.ThrowIfCancellationRequested();
                        if (!repeat)
                            await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true, operation.Token);
                        CheckPlacementFeedback();
                        operation.Token.ThrowIfCancellationRequested();
                        Station.GetAssembly(job, target);
                        checkingPcbPresence = false;
                        _repeatTrip = null;
                        await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false, operation.Token);
                        await SetLiftDownAsync(false, operation.Token);
                        await MoveToHorizontalZAsync(operation.Token);
                        EnterStep(TargetHeatSink is null
                            ? PcbPlacementState.CompletingCarrier : PcbPlacementState.MovingToHandoff);
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
                    await PreparePlacementAsync(null, repeat, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    Station.Complete(job);
                    EnterStep(repeat ? PcbPlacementState.WaitingForCarrier : PcbPlacementState.MovingToHandoff);
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

    private async Task PreparePlacementAsync(HeatSinkSlot? departure, bool repeat, CancellationToken cancellationToken)
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
            await _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, carryingPcb && !repeat, preparation.Token);
            await SetLiftDownAsync(false, preparation.Token);
            await MoveToHorizontalZAsync(preparation.Token);
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
        await MoveToHorizontalZAsync(cancellationToken);
        await MoveAxisAsync(MotionAxis.X, _settings.HandoffPosition.X, cancellationToken);
        await MoveAxisAsync(MotionAxis.Y, _settings.HandoffPosition.Y, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _handoffPosition = new()
        {
            X = _settings.HandoffPosition.X, Y = _settings.HandoffPosition.Y, Z = _settings.HandoffPosition.Z,
        };
        EnterStep(returning ? PcbPlacementState.WaitingForSupplyReceipt : PcbPlacementState.WaitingForSupply);
    }

    public async Task PrepareReceiptAsync(CancellationToken cancellationToken = default, bool returning = false)
    {
        EnterStep(returning ? PcbPlacementState.PresentingToSupply : PcbPlacementState.ReceivingPcb);
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

    public Task MoveToHorizontalZAsync(CancellationToken cancellationToken = default)
    {
        return MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, cancellationToken);
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
        await MoveToHorizontalZAsync(cancellationToken);
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
                await MoveToHorizontalZAsync(cancellationToken);
                await MoveAxisAsync(MotionAxis.Y, position.Y, cancellationToken);
                await MoveAxisAsync(MotionAxis.X, position.X, cancellationToken);
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                break;
            case TeachMode.Full:
                if (!double.IsFinite(position.Z))
                    throw new ArgumentOutOfRangeException(nameof(position), "Target Z must be finite before XY movement.");
                await MoveToHorizontalZAsync(cancellationToken);
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
        if (down && _motion.IsMoving)
            throw new MotionInterlockException("Stop the placement axes before lowering the handler.");
        return _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerDown, down, cancellationToken);
    }

    public async Task SetVacuumAsync(bool on, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.PcbPlacementVacuumEjector, on);
        await _io.WaitForInputAsync(InputIo.PcbPlacementVacuumDetected, on, cancellationToken);
    }

    public Task WaitForPcbAsync(CancellationToken cancellationToken = default)
    {
        return _io.WaitForInputAsync(InputIo.PcbPlacementPcbDetected, true, cancellationToken);
    }

    private void EnsureHandlerRaised(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HandlerRaised)
        {
            throw new MotionInterlockException("Raise the placement handler before moving any axis.");
        }
    }
}
