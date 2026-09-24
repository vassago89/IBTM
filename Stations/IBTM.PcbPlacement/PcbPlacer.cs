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
    private readonly IXyMotion _motion;
    private readonly IIoService _io;
    private readonly PcbPlacementHandlerSettings _settings;
    private readonly IPcbSupplyHandoff _supply;
    private readonly PcbPlacementWork _work;
    private readonly RecipeManager _recipes;
    private readonly UnitSettings _units;

    private HeatSinkSlot[]? _runTargets;
    private PcbPlacementState _state;
    private bool _handoffReady;

    public PcbPlacer(
        IXyMotion motion,
        IIoService io,
        PcbPlacementHandlerSettings settings,
        IPcbSupplyHandoff supply,
        PcbPlacementWork work,
        RecipeManager recipes,
        UnitSettings units)
    {
        _motion = motion;
        _io = io;
        _settings = settings;
        _supply = supply;
        _work = work;
        _recipes = recipes;
        _units = units;
        _state = PcbPlacementState.MovingToHandoff;
        Motion = new(motion);
        io.InputChanged += OnInputChanged;
        motion.MovingChanged += OnMovingChanged;
        motion.StateChanged += NotifyChanged;
        work.Changed += NotifyChanged;
        work.Station.CarrierChanged += OnCarrierChanged;
    }

    public override event Action? Changed;

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private void OnMovingChanged(bool moving)
    {
        if (moving)
            _handoffReady = false;
    }

    public MotionStatus Motion { get; }

    public IMotionFeedback Feedback => _motion;

    public bool IsAtHandoff(bool live = true)
    {
        return Motion.IsAt(_settings.HandoffPosition, live);
    }

    public bool IsAtReceivePosition(bool live = true)
    {
        return _settings.ReceiveZ is { } z
            && Motion.IsAt(new() { X = _settings.HandoffPosition.X, Y = _settings.HandoffPosition.Y, Z = z }, live);
    }

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

            return VacuumDetected
                ? PlacementPcbState.Secured
                : PlacementPcbState.Detected;
        }
    }

    public bool PcbSecured => Pcb == PlacementPcbState.Secured;

    public bool VacuumDetected => _io.GetInput(InputIo.PcbPlacementVacuumDetected);

    public bool IsAtHorizontalZ(bool live = true)
    {
        return Motion.IsAtZ(_settings.HandoffPosition.Z, live)
            && Motion.IsSettled(live, MotionAxis.Z);
    }

    public void InitializeMotion()
    {
        _motion.Initialize();
    }

    public void StopMotion()
    {
        _motion.Stop();
    }

    public Task ResetMotionAsync(CancellationToken cancellationToken = default)
    {
        return _motion.ResetAsync(cancellationToken);
    }

    public void SetServo(MotionAxis axis, bool on)
    {
        _motion.SetServo(axis, on);
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

    public bool IsAtXY(AxisPosition position, bool live = true)
    {
        if (!Motion.IsSettled(live, MotionAxis.X, MotionAxis.Y))
            return false;
        var current = Motion.ReadPosition(live);
        return Math.Abs(current.X - position.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - position.Y) <= MotionService.PositionToleranceMillimeters;
    }

    public bool IsAtZ(AxisPosition position, bool live = true)
    {
        return Motion.IsSettled(live, MotionAxis.Z)
            && Math.Abs(Motion.ReadPosition(live).Z - position.Z) <= MotionService.PositionToleranceMillimeters;
    }

    public Task MoveToHorizontalZAsync(CancellationToken cancellationToken = default)
    {
        return MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, cancellationToken);
    }

    public async Task MoveToHandoffXYAsync(CancellationToken cancellationToken = default)
    {
        State = PcbPlacementState.MovingToHandoff;
        await MoveToHorizontalZAsync(cancellationToken);
        await MoveAxisAsync(MotionAxis.X, _settings.HandoffPosition.X, cancellationToken);
        await MoveAxisAsync(MotionAxis.Y, _settings.HandoffPosition.Y, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _handoffReady = true;
        State = PcbPlacementState.WaitingForSupply;
    }

    public bool IsAtY(AxisPosition position, bool live = true)
    {
        return Motion.IsSettled(live, MotionAxis.Y)
            && Math.Abs(Motion.ReadPosition(live).Y - position.Y) <= MotionService.PositionToleranceMillimeters;
    }

    public async Task MoveToReceiveZAsync(CancellationToken cancellationToken = default)
    {
        State = PcbPlacementState.ReceivingPcb;
        await MoveAxisAsync(
            MotionAxis.Z,
            _settings.ReceiveZ ?? throw new MotionInterlockException("Teach PCB Receive Z before receiving a PCB."),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _handoffReady = true;
        if (PcbSecured)
            State = PcbPlacementState.WaitingForSupplyRelease;
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

    public Task SetIpmLiftDownAsync(bool down, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, down, cancellationToken);
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

    public PcbPlacementState State
    {
        get => _work.Enabled ? _state : PcbPlacementState.Disabled;
        private set
        {
            if (_state == value)
                return;
            _state = value;
            Changed?.Invoke();
        }
    }

    public PcbPlacementHandoff Handoff
    {
        get
        {
            if (!_handoffReady || !HandlerRaised || IpmLift == PlacementCylinderState.Between
                || !Motion.IsReady(live: true)
                || !_motion.GetAxisState(MotionAxis.X).Homed
                || !_motion.GetAxisState(MotionAxis.Y).Homed
                || !_motion.GetAxisState(MotionAxis.Z).Homed
                || !Motion.IsSettled(true, MotionAxis.X, MotionAxis.Y, MotionAxis.Z))
                return PcbPlacementHandoff.Unavailable;
            switch (State)
            {
                case PcbPlacementState.WaitingForSupplyRelease when PcbSecured:
                    return _repeat && _repeatTrip?.State == PcbPlacementState.ReceivingPcb
                        ? PcbPlacementHandoff.Returning : PcbPlacementHandoff.Holding;
                case PcbPlacementState.WaitingForSupply when !PcbSecured:
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
                if (_repeat && !_units.MainConveyor && _work.Completed
                    && _work.Station.CarrierSeated && State == PcbPlacementState.WaitingForCarrier)
                    _work.StartRepeat(_work.CurrentJob);
                if (_work.Station.CarrierSeated)
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
            _runTargets = null;
            _repeatTrip = null;
            _repeat = false;
            EndRun(cancellationToken);
        }
    }

    // A state owns the whole operation. False means an external handoff/carrier wait.
    public async Task<bool> PlaceAsync(
        HeatSinkSlot? heatSink,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_work.Enabled)
            return false;
        var job = _work.CurrentJob;
        var state = State;
        switch (state)
        {
            case PcbPlacementState.WaitingForSupply when _supply.Handoff == PcbSupplyHandoff.Holding:
                state = PcbPlacementState.ReceivingPcb;
                break;
            case PcbPlacementState.WaitingForSupplyRelease when _supply.Handoff == PcbSupplyHandoff.Released:
                state = PcbPlacementState.PreparingPlacement;
                break;
            case PcbPlacementState.WaitingForCarrier when _work.Station.CarrierSeated && !_work.Completed:
                state = heatSink is null ? PcbPlacementState.CompletingCarrier : PcbPlacementState.PlacingPcb;
                break;
        }
        State = state;
        TraceStep(state, _repeatTrip is { } trip ? $"{trip.HeatSink}, Repeat {trip.State}" : heatSink?.ToString(), job.Id);
        if (IsPcbGripUncertain)
            throw new InvalidOperationException("Placement PCB holding is uncertain away from a confirmed support. Check vacuum and PCB detection before moving or releasing it.");
        switch (state)
        {
            case PcbPlacementState.MovingToHandoff:
                await SetLiftDownAsync(false, cancellationToken);
                await MoveToHorizontalZAsync(cancellationToken);
                await SetIpmLiftDownAsync(!_repeat, cancellationToken);
                await MoveToHandoffXYAsync(cancellationToken);
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
                    if (_repeatTrip is { } releasing)
                        releasing.State = PcbPlacementState.PreparingPlacement;
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
            await MoveToReceiveZAsync(receipt.Token);
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
            _handoffReady = true;
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

    private void OnCarrierChanged(bool present)
    {
        _runTargets = null;
    }

    private bool IsPcbGripUncertain
    {
        get
        {
            var pcb = Pcb;
            if (pcb == PlacementPcbState.Secured || !VacuumDetected)
                return false;
            if (State is PcbPlacementState.ReceivingPcb or PcbPlacementState.WaitingForSupplyRelease
                && _supply.Handoff == PcbSupplyHandoff.Holding)
                return false;
            return true;
        }
    }

    private bool IsTarget(HeatSinkSlot heatSink)
    {
        return _runTargets?.Contains(heatSink) ?? _work.Station.IsHeatSinkPresent(heatSink);
    }

    private bool IsHeatSinkCompleted(HeatSinkSlot heatSink)
    {
        return _work.Assemblies.Any(assembly => assembly.HeatSink == heatSink);
    }

    private AxisPosition GetHeatSinkPosition(HeatSinkSlot heatSink)
    {
        var recipe = _recipes.Current.PcbPlacement;
        return heatSink == HeatSinkSlot.HeatSink1
            ? recipe.HeatSink1PcbPlacementPosition
            : recipe.HeatSink2PcbPlacementPosition;
    }
}
