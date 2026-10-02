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
        _recipes = recipes;
        _units = units;
        Motion = motionStatus;
        Phase = PcbPlacementState.MovingToHandoff;
        motion.StateChanged += NotifyChanged;
        station.Changed += NotifyChanged;
        StepChanged += NotifyChanged;
    }

    public ConveyorStation Station { get; }

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
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        if (_units.PcbPlacement && (Pcb != PlacementPcbState.None || Io.GetInput(InputIo.PcbPlacementVacuumDetected)))
            throw new InvalidOperationException("Clear the Placement PCB and vacuum before starting a new run.");
        _runTargets = null;
        _targetIndex = 0;
        Phase = _units.PcbPlacement ? PcbPlacementState.Retracting
            : PcbPlacementState.WaitingForCarrier;
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
                await MoveToStandbyAsync(TargetHeatSink ?? HeatSinkSlot.HeatSink1, operation.Token);
                EnterStep(PcbPlacementState.WaitingForCarrier);
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                operation.Token.ThrowIfCancellationRequested();
                if (Station.Completed)
                {
                    _runTargets = null;
                    _targetIndex = 0;
                }
                var heatSink = TargetHeatSink;
                var step = GetNextStep(heatSink);
                if (!await ExecuteStepAsync(step, heatSink, operation.Token))
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
            ActivePcb = null;
            EndRun(cancellationToken);
        }
    }

    internal PcbPlacementState GetNextStep(HeatSinkSlot? heatSink)
    {
        if (!_units.PcbPlacement)
            return PcbPlacementState.Disabled;
        if (Station.Completed)
            return PcbPlacementState.WaitingForCarrier;
        // Completed includes live presence; recheck it after reading seating feedback below.
        var state = Phase;
        switch (state)
        {
            case PcbPlacementState.WaitingForSupplyDeparture when _supply.Handoff == PcbSupplyHandoff.Unavailable:
                return PcbPlacementState.MovingToHandoff;
            case PcbPlacementState.WaitingForSupplyRelease when _supply.Handoff == PcbSupplyHandoff.Released:
                return PcbPlacementState.PreparingPlacement;
            case PcbPlacementState.WaitingForCarrier when Station.CarrierSeated && !Station.Completed:
                if (heatSink is null)
                    return PcbPlacementState.Retracting;
                return PcbPlacementState.MovingToHandoff;
            default:
                return state;
        }
    }

    internal async Task<bool> ExecuteStepAsync(
        PcbPlacementState state,
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (state == PcbPlacementState.Disabled || !_units.PcbPlacement)
        {
            ActivePcb = null;
            if (Station.CarrierSeated && !PcbSecured && !IsPcbGripUncertain)
                Station.Complete();
            EnterStep(PcbPlacementState.Disabled, workId: Station.CurrentJob.Id,
                waitingFor: PcbSecured || IsPcbGripUncertain
                    ? "unfinished PCB handoff" : Station.Completed ? "carrier transfer" : "carrier seated");
            return false;
        }
        if (Station.CarrierSeated && !Station.Completed && _runTargets is null)
        {
            _runTargets = Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent).ToArray();
            _cycleStartedAt = Stopwatch.GetTimestamp();
        }
        var job = Station.CurrentJob;
        var activePcb = IsRunning ? heatSink : null;
        var targetChanged = ActivePcb != activePcb;
        ActivePcb = activePcb;
        EnterStep(state, heatSink?.ToString(), job.Id);
        if (targetChanged)
            NotifyChanged();
        var requiresSecuredPcb = state == PcbPlacementState.PreparingPlacement;
        if (IsPcbGripUncertain || requiresSecuredPcb && !PcbSecured)
            throw new InvalidOperationException("Placement PCB holding is uncertain away from a confirmed support. Check vacuum and PCB detection before moving or releasing it.");
        cancellationToken.ThrowIfCancellationRequested();
        switch (state)
        {
            case PcbPlacementState.MovingToHandoff or PcbPlacementState.ReceivingPcb:
                if (!Station.CarrierSeated || Station.Completed || heatSink is null)
                {
                    EnterStep(PcbPlacementState.WaitingForCarrier);
                    return false;
                }
                if (_supply.Handoff != PcbSupplyHandoff.Holding)
                    return false;
                await PrepareHandoffAsync(cancellationToken);
                break;
            case PcbPlacementState.PreparingPlacement:
                await MoveToStandbyAsync(
                    heatSink ?? throw new InvalidOperationException("No placement target is selected."),
                    cancellationToken);
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
                EnterStep(PcbPlacementState.PlacingPcb);
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
                    await Io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true, operation.Token);
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
                    await Io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true, operation.Token);
                    CheckPlacementFeedback();
                    operation.Token.ThrowIfCancellationRequested();
                    Station.GetAssembly(job, target);
                    _targetIndex++;
                    checkingPcbPresence = false;
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
                await MoveToStandbyAsync(null, cancellationToken);
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
                EnterStep(carrierComplete ? PcbPlacementState.WaitingForCarrier : PcbPlacementState.MovingToHandoff);
                break;
            case PcbPlacementState.WaitingForSupplyRelease or PcbPlacementState.WaitingForCarrier
                or PcbPlacementState.WaitingForSupplyDeparture:
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported PCB placement step.");
        }
        return true;
    }

    private async Task MoveToStandbyAsync(HeatSinkSlot? departure, CancellationToken cancellationToken)
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
            await Io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, carryingPcb, preparation.Token);
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
        CancellationToken cancellationToken = default)
    {
        EnterStep(PcbPlacementState.MovingToHandoff);
        using var approach = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiving = false;
        void CheckSupply()
        {
            // Supply publishes handoff only after arrival, with ready axes and rotation/grip feedback.
            if (_supply.Handoff != PcbSupplyHandoff.Holding
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
            await Io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true, approach.Token);
            await MoveAxisAsync(MotionAxis.X, _settings.HandoffPosition.X, approach.Token);
            await MoveAxisAsync(MotionAxis.Y, _settings.HandoffPosition.Y, approach.Token);
            CheckSupply();
            approach.Token.ThrowIfCancellationRequested();
            EnterStep(PcbPlacementState.ReceivingPcb);
            await MoveAxisAsync(
                MotionAxis.Z,
                _settings.ReceiveZ ?? throw new MotionInterlockException("Teach PCB Receive Z before receiving a PCB."),
                approach.Token);
            CheckSupply();
            approach.Token.ThrowIfCancellationRequested();
            receiving = true;
            CheckSupply();
            approach.Token.ThrowIfCancellationRequested();
            await Io.WaitForInputAsync(InputIo.PcbPlacementPcbDetected, true, approach.Token, requireCurrent: true);
            await SetVacuumAsync(true, approach.Token);
            approach.Token.ThrowIfCancellationRequested();
            if (!PcbSecured)
                throw new InvalidOperationException("Placement receipt requires both PCB detection and vacuum before Supply releases it.");
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
        if (PcbSecured)
            EnterStep(PcbPlacementState.WaitingForSupplyRelease);
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
            && Phase == PcbPlacementState.ReceivingPcb
            && _supply.Handoff == PcbSupplyHandoff.Holding
            && Lift == StationCylinderState.Up)
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
