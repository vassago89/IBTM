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
    private readonly PcbPlacementWork _work;
    private readonly UnitSettings _units;
    private HeatSinkSlot[]? _runTargets;
    // Down before release and Down after pressing have identical IO feedback.
    // Keep the press target only while this run owns the operation.
    private HeatSinkSlot? _pressingHeatSink;

    private readonly IXyMotion _motion;
    private readonly IIoService _io;
    private readonly PcbPlacementHandlerSettings _settings;

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
        Motion = new(motion);
        io.InputChanged += OnInputChanged;
        motion.StateChanged += NotifyChanged;
        work.Changed += NotifyChanged;
        work.Station.CarrierChanged += OnCarrierChanged;
    }

    public override event Action? Changed;

    private void NotifyChanged()
    {
        Changed?.Invoke();
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

    public PlacementGripperState IpmGripper
    {
        get
        {
            switch ((
                _io.GetInput(InputIo.PcbPlacementIpmGripperOpen),
                _io.GetInput(InputIo.PcbPlacementIpmGripperClosed)))
            {
                case (true, false):
                    return PlacementGripperState.Open;
                case (false, true):
                    return PlacementGripperState.Closed;
                default:
                    return PlacementGripperState.Between;
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

            return VacuumDetected && IpmGripper == PlacementGripperState.Closed
                ? PlacementPcbState.Secured
                : PlacementPcbState.Detected;
        }
    }

    public bool PcbSecured => Pcb == PlacementPcbState.Secured;

    public bool VacuumDetected => _io.GetInput(InputIo.PcbPlacementVacuumDetected);

    public bool IsAtHorizontalZ(bool live = true)
    {
        return live
            ? !_motion.IsMoving
                && _motion.GetAxisState(MotionAxis.Z).InPosition
                && _motion.IsAtHorizontalZ
            : !Motion.IsMoving
                && Motion.Axes[MotionAxis.Z].State is { InPosition: true }
                && Motion.IsAtZ(_settings.HandoffPosition.Z);
    }

    public bool IsAtHandoffXY(bool live = true)
    {
        return IsAtXY(_settings.HandoffPosition, live);
    }

    public void InitializeMotion()
    {
        _motion.Initialize();
    }

    public void StopMotion()
    {
        _motion.Stop();
    }

    public void ResetMotion()
    {
        _motion.Reset();
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

    public async Task MoveToHorizontalZAsync(CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        await _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    public Task MoveToHandoffXYAsync(CancellationToken cancellationToken = default)
    {
        return MoveToXYAsync(_settings.HandoffPosition, cancellationToken);
    }

    public bool IsAtY(AxisPosition position, bool live = true)
    {
        return Motion.IsSettled(live, MotionAxis.Y)
            && Math.Abs(Motion.ReadPosition(live).Y - position.Y) <= MotionService.PositionToleranceMillimeters;
    }

    public Task MoveToReceiveZAsync(CancellationToken cancellationToken = default)
    {
        return MoveAxisAsync(
            MotionAxis.Z,
            _settings.ReceiveZ ?? throw new MotionInterlockException("Teach PCB Receive Z before receiving a PCB."),
            cancellationToken);
    }

    public Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        CancellationToken cancellationToken = default)
    {
        EnsureHandlerRaised(cancellationToken);
        var speed = axis == MotionAxis.Z ? _settings.Motion.ZSpeed : _settings.Motion.HorizontalSpeed;
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

    public async Task MoveToAsync(double x, double y, double z, CancellationToken cancellationToken = default)
    {
        await MoveToHorizontalZAsync(cancellationToken);
        EnsureHandlerRaised(cancellationToken);
        await _motion.MoveToAsync(x, y, z, cancellationToken);
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
                await _motion.MoveToHorizontalZAsync(cancellationToken, travelZ: position.Z);
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
                await MoveToAsync(position.X, position.Y, position.Z, cancellationToken);
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

    public Task SetIpmGripperAsync(bool closed, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmGripperClose, closed, cancellationToken);
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
            or InputIo.PcbPlacementVacuumDetected
            or InputIo.PcbPlacementIpmGripperClosed
            or InputIo.PcbPlacementIpmGripperOpen)
        {
            Changed?.Invoke();
        }
    }

    public PcbPlacementState State => GetState();

    public PcbPlacementHandoff Handoff
    {
        get
        {
            if (_repeatTrip is { } trip && _units.PcbSupply)
            {
                if (HandlerRaised && IpmLift == PlacementCylinderState.Down
                    && IsAtReceivePosition() && PcbSecured)
                {
                    return trip.State == PcbPlacementState.ReceivingPcb ? PcbPlacementHandoff.Returning
                        : trip.State == PcbPlacementState.WaitingForSupply ? PcbPlacementHandoff.Holding
                        : PcbPlacementHandoff.Unavailable;
                }
                return HandlerRaised && IsAtHorizontalZ()
                    && IsAtY(GetHeatSinkPosition(trip.HeatSink))
                    ? PcbPlacementHandoff.Clear : PcbPlacementHandoff.Unavailable;
            }
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
                    if (_repeat && !_units.MainConveyor && _work.Completed
                        && _work.Station.CarrierSeated && HandlerRaised && IsAtHorizontalZ())
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
        TraceStep(state, _repeatTrip is { } trip ? $"{trip.HeatSink}, Repeat {trip.State}" : heatSink?.ToString(), job.Id);
        if (IsPcbGripUncertain)
            throw new InvalidOperationException("Placement PCB grip is incomplete away from a confirmed support. Check vacuum and gripper feedback before moving or releasing it.");
        switch (state)
        {
            case PcbPlacementState.MovingToHandoff:
                await SetLiftDownAsync(false, cancellationToken);
                await MoveToHorizontalZAsync(cancellationToken);
                if (_repeatTrip?.State != PcbPlacementState.MovingToHandoff)
                    await SetIpmGripperAsync(false, cancellationToken);
                await SetIpmLiftDownAsync(true, cancellationToken);
                await MoveToHandoffXYAsync(cancellationToken);
                break;
            case PcbPlacementState.ReceivingPcb:
                if (!await ReceivePcbAsync(cancellationToken))
                    return false;
                break;
            case PcbPlacementState.PreparingPlacement:
                await PreparePlacementAsync(heatSink ?? HeatSinkSlot.HeatSink1, cancellationToken);
                break;
            case PcbPlacementState.PlacingPcb:
            {
                var carryingPcb = PcbSecured;
                var target = carryingPcb
                    ? heatSink!.Value
                    : GetCurrentHeatSink()!.Value;
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                void CheckPlacementFeedback()
                {
                    if (!_work.Station.CarrierSeated || !ReferenceEquals(job, _work.CurrentJob)
                        || carryingPcb && !PcbSecured
                        || _pressingHeatSink == target && Pcb == PlacementPcbState.None)
                        operation.Cancel();
                }
                Changed += CheckPlacementFeedback;
                try
                {
                    CheckPlacementFeedback();
                    operation.Token.ThrowIfCancellationRequested();
                    if (carryingPcb)
                    {
                        var position = GetHeatSinkPosition(target);
                        await SetIpmLiftDownAsync(true, operation.Token);
                        if (!IsAtXY(position) || !IsAtZ(position))
                        {
                            await SetLiftDownAsync(false, operation.Token);
                            if (!IsAtXY(position))
                                await MoveToXYAsync(position, operation.Token);
                            await MoveAxisAsync(MotionAxis.Z, position.Z, operation.Token);
                        }
                        await SetLiftDownAsync(true, operation.Token);
                        _pressingHeatSink = null;
                        if (_repeatTrip is { } releasing)
                            releasing.State = PcbPlacementState.PreparingPlacement;
                        // Grip is no longer required after confirmed placement descent.
                        carryingPcb = false;
                        await SetVacuumAsync(false, operation.Token);
                    }

                    if (_pressingHeatSink != target)
                    {
                        await SetIpmGripperAsync(false, operation.Token);
                        await SetIpmLiftDownAsync(false, operation.Token);
                        _pressingHeatSink = target;
                    }
                    CheckPlacementFeedback();
                    operation.Token.ThrowIfCancellationRequested();
                    await SetIpmGripperAsync(true, operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    if (_pressingHeatSink != target || !_work.Station.CarrierSeated
                        || Pcb == PlacementPcbState.None)
                        throw new InvalidOperationException("The placement carrier changed or lost seating before the PCB press.");
                    await SetIpmLiftDownAsync(true, operation.Token);
                    CheckPlacementFeedback();
                    operation.Token.ThrowIfCancellationRequested();
                    _work.GetAssembly(job, target);
                    _pressingHeatSink = null;
                    _repeatTrip = null;
                    await SetIpmLiftDownAsync(false, operation.Token);
                    await SetLiftDownAsync(false, operation.Token);
                    await MoveToHorizontalZAsync(operation.Token);
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
            if (IpmGripper != PlacementGripperState.Open)
                await SetIpmGripperAsync(false, receipt.Token);
            if (IpmLift != PlacementCylinderState.Down)
                await SetIpmLiftDownAsync(true, receipt.Token);
            await MoveToReceiveZAsync(receipt.Token);
            await WaitForPcbAsync(receipt.Token);
            await SetVacuumAsync(true, receipt.Token);
            await SetIpmGripperAsync(true, receipt.Token);
            receipt.Token.ThrowIfCancellationRequested();
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
            await SetIpmLiftDownAsync(carryingPcb, preparation.Token);
            await SetLiftDownAsync(false, preparation.Token);
            await MoveToHorizontalZAsync(preparation.Token);
            if (carryingPcb && departure is { } destination)
                await MoveAxisAsync(MotionAxis.Y, GetHeatSinkPosition(destination).Y, preparation.Token);
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

    public PcbPlacementState GetState(bool live = true)
    {
        return GetState(TargetHeatSink, live);
    }

    public PcbPlacementState GetState(HeatSinkSlot? heatSink, bool live = true)
    {
        if (!_work.Enabled)
            return PcbPlacementState.Disabled;
        if (_repeatTrip?.State is PcbPlacementState.ReceivingPcb
            or PcbPlacementState.WaitingForSupplyRelease or PcbPlacementState.WaitingForSupply)
            return _repeatTrip.State;
        var pcb = Pcb;
        var currentHeatSink = GetCurrentHeatSink(live);
        if (currentHeatSink is { } current && _work.Station.CarrierSeated && !VacuumDetected)
        {
            if (IsHeatSinkCompleted(current))
            {
                if (Lift != PlacementCylinderState.Up || !IsAtHorizontalZ(live))
                    return PcbPlacementState.PreparingPlacement;
            }
            else if ((!_repeat || _repeatTrip?.State is PcbPlacementState.PlacingPcb or PcbPlacementState.PreparingPlacement)
                && !_work.Completed && IsTarget(current) && pcb != PlacementPcbState.None
                && IsAtZ(GetHeatSinkPosition(current), live)
                && Lift == PlacementCylinderState.Down)
            {
                return PcbPlacementState.PlacingPcb;
            }
        }

        switch (true)
        {
            case true when _repeatTrip?.State == PcbPlacementState.PickingPcb:
                return PcbPlacementState.PickingPcb;
            case true when _repeatTrip?.State == PcbPlacementState.MovingToHandoff:
                return PcbPlacementState.MovingToHandoff;
            case true when pcb == PlacementPcbState.Secured:
                switch (true)
                {
                    case true when !_repeat && IsAtReceivePosition(live):
                        return HandlerRaised && IpmLift == PlacementCylinderState.Down
                            ? PcbPlacementState.WaitingForSupplyRelease
                            : PcbPlacementState.ReceivingPcb;
                    case true when _work.Station.CarrierSeated && heatSink is not null
                        && IsAtXY(GetHeatSinkPosition(heatSink.Value), live):
                        return PcbPlacementState.PlacingPcb;
                    case true when IpmLift != PlacementCylinderState.Down
                        || Lift != PlacementCylinderState.Up || !IsAtHorizontalZ(live):
                        return PcbPlacementState.PreparingPlacement;
                    case true when !IsAtY(GetHeatSinkPosition(heatSink ?? HeatSinkSlot.HeatSink1), live):
                        return PcbPlacementState.PreparingPlacement;
                    case true when !_work.Station.CarrierSeated || _work.Completed:
                        return PcbPlacementState.WaitingForCarrier;
                    default:
                        return heatSink is null ? PcbPlacementState.CompletingCarrier : PcbPlacementState.PlacingPcb;
                }
            case true when _work.Station.CarrierSeated && !_work.Completed && heatSink is null:
                return Lift == PlacementCylinderState.Up && IsAtHorizontalZ(live)
                    ? PcbPlacementState.CompletingCarrier
                    : PcbPlacementState.PreparingPlacement;
            case true when _repeat:
                return PcbPlacementState.WaitingForCarrier;
        }

        var atHandoff = IsAtHandoff(live);
        switch (true)
        {
            case true when IsAtReceivePosition(live) && !atHandoff:
                return PcbPlacementState.ReceivingPcb;
            case true when !atHandoff:
                return PcbPlacementState.MovingToHandoff;
            case true when Lift != PlacementCylinderState.Up:
                return PcbPlacementState.MovingToHandoff;
            case true when IpmGripper != PlacementGripperState.Open || IpmLift != PlacementCylinderState.Down:
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

    private bool IsPcbGripUncertain
    {
        get
        {
            var pcb = Pcb;
            if (pcb == PlacementPcbState.Secured
                || !VacuumDetected
                    && (pcb == PlacementPcbState.None || IpmGripper == PlacementGripperState.Open))
                return false;
            if (IsAtReceivePosition() && _supply.Handoff == PcbSupplyHandoff.Holding)
                return false;
            if (!VacuumDetected && _work.Station.CarrierSeated
                && GetCurrentHeatSink() is { } heatSink
                && (Lift == PlacementCylinderState.Down
                        && IsAtZ(GetHeatSinkPosition(heatSink))
                    || IsHeatSinkCompleted(heatSink)))
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

    private HeatSinkSlot? GetCurrentHeatSink(bool live = true)
    {
        var recipe = _recipes.Current.PcbPlacement;
        if (IsAtXY(recipe.HeatSink1PcbPlacementPosition, live))
        {
            return HeatSinkSlot.HeatSink1;
        }

        return IsAtXY(recipe.HeatSink2PcbPlacementPosition, live) ? HeatSinkSlot.HeatSink2 : null;
    }

    private AxisPosition GetHeatSinkPosition(HeatSinkSlot heatSink)
    {
        var recipe = _recipes.Current.PcbPlacement;
        return heatSink == HeatSinkSlot.HeatSink1
            ? recipe.HeatSink1PcbPlacementPosition
            : recipe.HeatSink2PcbPlacementPosition;
    }
}
