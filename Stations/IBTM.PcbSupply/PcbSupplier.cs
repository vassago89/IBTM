using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.PcbSupply;

public sealed class PcbSupplier : AutoUnit, IPcbSupplyHandoff
{
    private readonly IXyMotion _motion;
    private readonly IIoService _io;
    private readonly PcbSupplySettings _settings;
    private readonly UnitSettings _units;
    private readonly RecipeManager _recipes;
    // Slot progress belongs only to the current run and upstream carrier.
    private PickStep _pickStep;
    private bool _repeat;
    private bool _handoffPendingDeparture;
    // Completed stage position, invalidated by motion/state changes; never proof of current readiness.
    private AxisPosition? _handoffPosition;
    // Commissioning input, kept only for this application session.
    private volatile bool _testUpstreamCarrierAvailable;

    public PcbSupplier(
        IXyMotion motion,
        MotionStatus motionStatus,
        IIoService io,
        PcbSupplySettings settings,
        RecipeManager recipes,
        UnitSettings units)
    {
        _motion = motion;
        _io = io;
        _settings = settings;
        _units = units;
        _recipes = recipes;
        Motion = motionStatus;
        Phase = PcbSupplyState.MovingToPickup;
        io.InputChanged += OnInputChanged;
        motion.StateChanged += OnMotionStateChanged;
        StepChanged += NotifyChanged;
    }

    private void OnMotionStateChanged()
    {
        _handoffPosition = null;
        NotifyChanged();
    }

    public MotionStatus Motion { get; }

    public bool UpstreamCarrierAvailable
    {
        get
        {
            return _io.GetInput(InputIo.AutoMode)
                ? _testUpstreamCarrierAvailable
                : _io.GetInput(InputIo.PcbSupplyAvailableFromFront1);
        }
    }

    public bool TestUpstreamCarrierAvailable
    {
        get => _testUpstreamCarrierAvailable;
        set
        {
            // The selector contact is ON in teaching/manual mode.
            value = value && _io.IsReady && _io.GetInput(InputIo.AutoMode);
            if (_testUpstreamCarrierAvailable == value)
                return;
            _testUpstreamCarrierAvailable = value;
            OnHandlerChanged();
            NotifyChanged();
        }
    }

    public PcbSupplyCylinderState Gripper
    {
        get
        {
            switch ((_io.GetInput(InputIo.PcbSupplyGripperOpen), _io.GetInput(InputIo.PcbSupplyGripperClosed)))
            {
                case (true, false):
                    return PcbSupplyCylinderState.Backward;
                case (false, true):
                    return PcbSupplyCylinderState.Forward;
                default:
                    return PcbSupplyCylinderState.Between;
            }
        }
    }

    public PcbSupplyPcbState Pcb
    {
        get
        {
            if (!_io.GetInput(InputIo.PcbSupplyPcbDetected))
            {
                return PcbSupplyPcbState.None;
            }

            return Gripper == PcbSupplyCylinderState.Forward
                && _io.GetInput(InputIo.PcbSupplyIpmFixerForward)
                ? PcbSupplyPcbState.Secured
                : PcbSupplyPcbState.Detected;
        }
    }

    public PcbSupplyRotationState Rotation
    {
        get
        {
            switch ((_io.GetInput(InputIo.PcbSupplyUnrotated), _io.GetInput(InputIo.PcbSupplyRotated)))
            {
                case (true, false):
                    return PcbSupplyRotationState.Unrotated;
                case (false, true):
                    return PcbSupplyRotationState.Rotated;
                default:
                    return PcbSupplyRotationState.Between;
            }
        }
    }

    public bool PcbSecured => Pcb == PcbSupplyPcbState.Secured;

    public bool PcbReleased
    {
        get
        {
            return Gripper == PcbSupplyCylinderState.Backward
                && !_io.GetInput(InputIo.PcbSupplyIpmFixerForward);
        }
    }

    public bool IsAtPickupXY(PcbPickPosition position)
    {
        if (position.Y is not { } y)
            return false;
        var current = _motion.Position;
        return MotionService.IsSettled(_motion, MotionAxis.X, MotionAxis.Y)
            && Math.Abs(current.X - position.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - y) <= MotionService.PositionToleranceMillimeters;
    }

    public void SetUpstreamReady(bool ready)
    {
        if (!_io.GetInput(InputIo.AutoMode))
            _io.SetOutput(OutputIo.PcbSupplyReadyToFront1, ready);
    }

    public void StopUpstream()
    {
        // In production, STOP is not pickup completion. Teaching clears the external
        // output; unknown production feedback must not imply an empty carrier position.
        if (!_io.IsReady)
            throw new IOException("Supply SMEMA feedback is unavailable; Ready cannot be cleared safely.");
        if (_io.GetInput(InputIo.AutoMode) || !UpstreamCarrierAvailable)
            _io.SetOutput(OutputIo.PcbSupplyReadyToFront1, false);
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input == InputIo.AutoMode && !value)
            _testUpstreamCarrierAvailable = false;

        if (input is InputIo.AutoMode
            or InputIo.PcbSupplyAvailableFromFront1
            or InputIo.PcbSupplyUnrotated
            or InputIo.PcbSupplyRotated
            or InputIo.PcbSupplyGripperClosed
            or InputIo.PcbSupplyGripperOpen
            or InputIo.PcbSupplyIpmFixerForward
            or InputIo.PcbSupplyPcbDetected)
        {
            OnHandlerChanged();
            NotifyChanged();
        }
    }

    private void OnHandlerChanged()
    {
        if (_units.PcbSupply && !_repeat && _pickStep != PickStep.Pcb1
            && Phase is PcbSupplyState.MovingToPickup or PcbSupplyState.WaitingForCarrier or PcbSupplyState.WaitingForCarrierExit
            && !UpstreamCarrierAvailable)
        {
            _pickStep = PickStep.Pcb1;
        }
    }

    public PcbSupplyHandoff Handoff
    {
        get
        {
            var position = _handoffPosition;
            if (position is null || !MotionService.IsHoldingPosition(_motion, position))
                return PcbSupplyHandoff.Unavailable;
            if (!_units.PcbSupply || Rotation != PcbSupplyRotationState.Unrotated
                || Phase is not (PcbSupplyState.HandingOff or PcbSupplyState.WaitingForPlacementClear
                    or PcbSupplyState.WaitingForReturnedPcbGrip or PcbSupplyState.WaitingForReturnClear))
                return PcbSupplyHandoff.Unavailable;
            return PcbSecured ? PcbSupplyHandoff.Holding
                : PcbReleased ? PcbSupplyHandoff.Released : PcbSupplyHandoff.Unavailable;
        }
    }

    // Retained handoff phase; it is not proof of physical position or an active run.
    public PcbSupplyState Phase { get; private set; }

    private void EnterStep(
        PcbSupplyState step,
        string? target = null,
        long? workId = null,
        string? waitingFor = null)
    {
        var phaseChanged = step != PcbSupplyState.Disabled && Phase != step;
        if (phaseChanged)
            Phase = step;
        base.EnterStep(step, target ?? _pickStep.ToString(), workId, waitingFor);
        if (phaseChanged && !IsRunning)
            NotifyChanged();
    }

    private enum PickStep
    {
        Pcb1,
        Pcb2,
        WaitingForCarrierExit,
    }

    public async Task RunAsync(
        IPcbPlacementHandoff placement,
        CancellationToken cancellationToken = default,
        bool repeat = false)
    {
        Exception? failure = null;
        _repeat = repeat;
        try
        {
            var initialStep = Phase;
            if (repeat && _units.PcbSupply && _units.PcbPlacement && Phase == PcbSupplyState.HandingOff
                && (!PcbSecured || placement.Handoff == PcbPlacementHandoff.Returning)
                && placement.Handoff != PcbPlacementHandoff.Holding)
                initialStep = PcbSupplyState.WaitingForReturnedPcb;
            EnterStep(initialStep);
            BeginRun(_units.PcbSupply ? Phase : PcbSupplyState.Disabled);
            placement.Changed += WakeRun;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_units.PcbSupply && _handoffPosition is { } handoff && !MotionService.IsHoldingPosition(_motion, handoff))
                {
                    _handoffPosition = null;
                    NotifyChanged();
                }
                // A partial grip is valid only during pickup or an active handoff.
                var phase = _units.PcbSupply ? Phase : PcbSupplyState.Disabled;
                if (Pcb == PcbSupplyPcbState.Detected && !PcbReleased
                    && phase != PcbSupplyState.PickingPcb
                    && !(phase == PcbSupplyState.HandingOff
                        && placement.Handoff is PcbPlacementHandoff.Holding or PcbPlacementHandoff.Returning))
                {
                    throw new InvalidOperationException(
                        "Supply PCB grip is incomplete away from a confirmed support. Check gripper and IPM fixation before moving or releasing it.");
                }
                var step = GetNextStep(placement, repeat);
                if (!await ExecuteStepAsync(step, placement, repeat, cancellationToken))
                    await WaitForChangeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            placement.Changed -= WakeRun;
            if (!repeat)
                _pickStep = PickStep.Pcb1;
            _repeat = false;
            try
            {
                StopUpstream();
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
            finally
            {
                EndRun(cancellationToken);
            }
        }
    }

    internal PcbSupplyState GetNextStep(IPcbPlacementHandoff placement, bool repeat = false)
    {
        if (!_units.PcbSupply)
            return PcbSupplyState.Disabled;
        var state = Phase;
        if (repeat)
        {
            switch (state)
            {
                case PcbSupplyState.WaitingForReturnedPcb:
                    return placement.ReturningPcb is not null
                        ? PcbSupplyState.PreparingReturnReceipt : state;
                case PcbSupplyState.WaitingForReturnedPcbGrip:
                    return placement.Handoff == PcbPlacementHandoff.Returning
                        ? PcbSupplyState.ReceivingReturnedPcb : state;
                case PcbSupplyState.WaitingForReturnClear:
                    return placement.Handoff == PcbPlacementHandoff.Clear
                        ? PcbSupplyState.ReturningToPickup : state;
                case PcbSupplyState.PreparingReturnReceipt or PcbSupplyState.ReceivingReturnedPcb
                    or PcbSupplyState.ReturningToPickup:
                    return state;
                case PcbSupplyState.HandingOff:
                    if (!_units.PcbPlacement)
                        return PcbSupplyState.ReturningToPickup;
                    return state;
                case PcbSupplyState.WaitingForPlacementClear:
                    return placement.Handoff == PcbPlacementHandoff.Clear
                        ? PcbSupplyState.WaitingForReturnedPcb : state;
                default:
                    if (PcbSecured)
                        return PcbSupplyState.MovingToHandoff;
                    if (_units.PcbPlacement)
                        return placement.ReturningPcb is not null
                            ? PcbSupplyState.PreparingReturnReceipt : PcbSupplyState.WaitingForReturnedPcb;
                    break;
            }
        }
        switch (state)
        {
            case PcbSupplyState.WaitingForCarrier when UpstreamCarrierAvailable:
                return PcbSupplyState.PickingPcb;
            case PcbSupplyState.WaitingForPlacementClear when placement.Handoff == PcbPlacementHandoff.Clear:
            case PcbSupplyState.WaitingForCarrierExit when _pickStep == PickStep.Pcb1 || !UpstreamCarrierAvailable:
                return PcbSupplyState.MovingToPickup;
            default:
                return state;
        }
    }

    private async Task<bool> ExecuteStepAsync(
        PcbSupplyState step,
        IPcbPlacementHandoff placement,
        bool repeat,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recipe = _recipes.Current.PcbSupply;
        if (step == PcbSupplyState.Disabled)
        {
            EnterStep(step);
            return false;
        }
        if (!repeat && Phase is PcbSupplyState.HandingOff or PcbSupplyState.WaitingForPlacementClear
            && Rotation != PcbSupplyRotationState.Unrotated)
            throw new MotionInterlockException("Supply handoff requires confirmed Unrotated feedback.");

        if ((!repeat || !_units.PcbPlacement) && _pickStep != PickStep.WaitingForCarrierExit)
        {
            SetUpstreamReady(true);
        }
        else if (Phase is PcbSupplyState.HandingOff or PcbSupplyState.WaitingForCarrierExit)
        {
            // Keep Ready through both slot checks and the final pickup lift.
            // Its falling edge tells the upstream machine that pickup is complete.
            SetUpstreamReady(false);
        }

        if (Phase == PcbSupplyState.WaitingForCarrierExit && step == PcbSupplyState.MovingToPickup)
            _pickStep = PickStep.Pcb1;
        var wasAtHandoff = _handoffPosition is { } handoffPosition
            && MotionService.IsHoldingPosition(_motion, handoffPosition)
            && Rotation == PcbSupplyRotationState.Unrotated;
        EnterStep(step, _pickStep.ToString());
        switch (step)
        {
            case PcbSupplyState.PreparingReturnReceipt:
                if (placement.ReturningPcb is not { } returnedPcb)
                    return false;
                _pickStep = returnedPcb == HeatSinkSlot.HeatSink2 ? PickStep.Pcb2 : PickStep.Pcb1;
                if (Rotation != PcbSupplyRotationState.Unrotated
                    && placement.Handoff is PcbPlacementHandoff.Returning or PcbPlacementHandoff.Holding)
                    throw new MotionInterlockException("Supply cannot prepare rotation while Placement holds the PCB at receive Z.");
                if (!PcbSecured)
                {
                    await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false, cancellationToken);
                    await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false, cancellationToken);
                }
                if (!wasAtHandoff)
                {
                    await SetRotatedAsync(false, cancellationToken);
                    await PrepareHandoffAsync(cancellationToken);
                }
                EnterStep(PcbSupplyState.WaitingForReturnedPcbGrip);
                break;
            case PcbSupplyState.ReceivingReturnedPcb:
                if (placement.Handoff != PcbPlacementHandoff.Returning || Pcb == PcbSupplyPcbState.None)
                    throw new InvalidOperationException("Supply must detect the returned PCB supported by placement before gripping it.");
                await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true, cancellationToken);
                await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true, cancellationToken);
                EnterStep(PcbSupplyState.WaitingForReturnClear);
                break;
            case PcbSupplyState.WaitingForReturnClear:
                if (!PcbSecured)
                    throw new InvalidOperationException("Supply lost the returned PCB before placement cleared the handoff.");
                return false;
            case PcbSupplyState.ReturningToPickup:
            {
                // Keep the returned PCB gripped; Repeat never places it upstream.
                var returnPosition = _pickStep == PickStep.Pcb1 ? recipe.Pcb1PickPosition : recipe.Pcb2PickPosition;
                using var returning = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                void CheckReturnHolding()
                {
                    if (!PcbSecured)
                        returning.Cancel();
                }
                Changed += CheckReturnHolding;
                try
                {
                    CheckReturnHolding();
                    returning.Token.ThrowIfCancellationRequested();
                    await MoveFromHandoffAsync(returnPosition, returning.Token);
                    await SetRotatedAsync(true, returning.Token);
                    await MoveToPickupAsync(returnPosition, returning.Token);
                    _handoffPendingDeparture = false;
                    EnterStep(PcbSupplyState.MovingToHandoff);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Supply lost PCB holding feedback during reverse travel.");
                }
                finally
                {
                    Changed -= CheckReturnHolding;
                }
                break;
            }
            case PcbSupplyState.MovingToPickup:
                var nextPick = _pickStep == PickStep.Pcb2 ? recipe.Pcb2PickPosition : recipe.Pcb1PickPosition;
                if (_handoffPendingDeparture)
                {
                    await MoveFromHandoffAsync(nextPick, cancellationToken);
                    await SetRotatedAsync(true, cancellationToken);
                    _handoffPendingDeparture = false;
                }
                else
                {
                    await SetRotatedAsync(true, cancellationToken);
                    await MoveToPickupAsync(nextPick, cancellationToken);
                }
                EnterStep(PcbSupplyState.WaitingForCarrier);
                if (_pickStep == PickStep.WaitingForCarrierExit)
                    EnterStep(PcbSupplyState.WaitingForCarrierExit);
                break;
            case PcbSupplyState.PickingPcb:
            {
                var pickStep = _pickStep;
                var pickPosition = pickStep == PickStep.Pcb1
                    ? recipe.Pcb1PickPosition
                    : recipe.Pcb2PickPosition;
                using var pickup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var carrierChanged = false;
                void StopWhenCarrierLeaves()
                {
                    if (!UpstreamCarrierAvailable)
                    {
                        carrierChanged = true;
                        pickup.Cancel();
                    }
                }

                Changed += StopWhenCarrierLeaves;
                try
                {
                    StopWhenCarrierLeaves();
                    EnterStep(PcbSupplyState.PickingPcb, pickStep.ToString());
                    await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false, pickup.Token);
                    await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false, pickup.Token);
                    EnterStep(PcbSupplyState.MovingToPickup);
                    await MoveToPickupAsync(pickPosition, pickup.Token);
                    await _motion.MoveAxisAsync(MotionAxis.Z, pickPosition.Z, _settings.Motion.ZSpeed, pickup.Token);
                    pickup.Token.ThrowIfCancellationRequested();
                    EnterStep(PcbSupplyState.PickingPcb);
                    if (Pcb == PcbSupplyPcbState.None)
                    {
                        await MoveAxisAsync(MotionAxis.Z, _settings.RotationZ, pickup.Token);
                        EnterStep(PcbSupplyState.WaitingForCarrier);
                    }
                    else
                    {
                        // Presence can be ON before reaching the PCB; grip only at the taught pickup XYZ.
                        await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true, pickup.Token);
                        await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true, pickup.Token);
                        pickup.Token.ThrowIfCancellationRequested();
                        EnterStep(PcbSupplyState.MovingToHandoff);
                    }

                    // Never advance a replacement carrier.
                    if (!carrierChanged)
                    {
                        if (repeat)
                        {
                            if (PcbSecured)
                                await MoveAxisAsync(MotionAxis.Z, _settings.RotationZ, pickup.Token);
                            else
                                _pickStep = pickStep == PickStep.Pcb1 ? PickStep.Pcb2 : PickStep.Pcb1;
                        }
                        else
                            _pickStep = pickStep == PickStep.Pcb1 ? PickStep.Pcb2 : PickStep.WaitingForCarrierExit;
                        if (!PcbSecured && _pickStep == PickStep.WaitingForCarrierExit)
                            EnterStep(PcbSupplyState.WaitingForCarrierExit);
                    }
                }
                catch (OperationCanceledException) when (carrierChanged
                    && !cancellationToken.IsCancellationRequested)
                {
                    if (repeat)
                        throw new InvalidOperationException("The upstream carrier left during the initial repeat pickup.");
                    _pickStep = PickStep.Pcb1;
                    EnterStep(PcbSupplyState.WaitingForCarrier);
                }
                finally
                {
                    Changed -= StopWhenCarrierLeaves;
                }
                break;
            }
            case PcbSupplyState.MovingToHandoff:
            {
                using var handoff = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                void CheckHolding()
                {
                    if (!PcbSecured)
                        handoff.Cancel();
                }
                Changed += CheckHolding;
                try
                {
                    CheckHolding();
                    handoff.Token.ThrowIfCancellationRequested();
                    if (Rotation != PcbSupplyRotationState.Unrotated)
                        await SetRotatedAsync(false, handoff.Token);
                    await PrepareHandoffAsync(handoff.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Supply lost PCB grip or IPM fixation during forward handoff.");
                }
                finally
                {
                    Changed -= CheckHolding;
                }
                break;
            }
            case PcbSupplyState.HandingOff:
                if (repeat && !PcbSecured && placement.Handoff != PcbPlacementHandoff.Holding)
                    throw new InvalidOperationException("Supply repeat lost PCB holding feedback during forward handoff.");
                if (PcbSecured && placement.Handoff != PcbPlacementHandoff.Holding)
                    return false;
                if (Rotation != PcbSupplyRotationState.Unrotated)
                    throw new MotionInterlockException("Supply must remain Unrotated while releasing the PCB.");
                if (_io.GetInput(InputIo.PcbSupplyIpmFixerForward))
                {
                    if (placement.Handoff != PcbPlacementHandoff.Holding)
                        throw new InvalidOperationException("Placement must detect and secure the PCB before supply releases its fixer.");
                    await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false, cancellationToken);
                }
                if (Gripper != PcbSupplyCylinderState.Backward)
                {
                    if (Rotation != PcbSupplyRotationState.Unrotated)
                        throw new MotionInterlockException("Supply lost Unrotated feedback before opening its gripper.");
                    if (placement.Handoff != PcbPlacementHandoff.Holding)
                        throw new InvalidOperationException("Placement lost PCB holding feedback before supply opened its gripper.");
                    await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false, cancellationToken);
                }
                EnterStep(PcbSupplyState.WaitingForPlacementClear);
                break;
            default:
                return false;
        }
        return true;
    }

    public async Task PrepareHandoffAsync(CancellationToken cancellationToken)
    {
        if (Rotation != PcbSupplyRotationState.Unrotated)
            throw new MotionInterlockException("Supply must be unrotated before moving to the handoff position.");
        var position = _settings.HandoffPosition;
        EnterStep(PcbSupplyState.MovingToHandoff);
        await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
        await _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _handoffPendingDeparture = true;
        _handoffPosition = new() { X = position.X, Y = position.Y, Z = position.Z };
        EnterStep(PcbSupplyState.HandingOff);
    }

    internal async Task MoveToPickupAsync(PcbPickPosition position, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (position.Y is not { } y)
            throw new MotionInterlockException("Teach the selected PCB pickup XYZ before moving Supply.");
        await MoveAxisAsync(MotionAxis.Z, _settings.RotationZ, cancellationToken);
        await _motion.MoveToXYAsync(
            position.X,
            y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public bool IsMoveToTeachingPositionAllowed(TeachingPosition point)
    {
        return point.Target switch
        {
            TeachingTarget.SupplyHandoff => Rotation == PcbSupplyRotationState.Unrotated,
            TeachingTarget.SupplyPcb1Pick or TeachingTarget.SupplyPcb2Pick
                => point.HasPosition && Rotation == PcbSupplyRotationState.Rotated,
            _ => true,
        };
    }

    public async Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (point.Mode)
        {
            case TeachMode.XOnly:
                await MoveAxisAsync(MotionAxis.X, position.X, cancellationToken);
                break;
            case TeachMode.YOnly:
                await MoveAxisAsync(MotionAxis.Y, position.Y, cancellationToken);
                break;
            case TeachMode.ZOnly:
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                break;
            case TeachMode.Full when point.Target is TeachingTarget.SupplyPcb1Pick or TeachingTarget.SupplyPcb2Pick:
                if (!point.HasPosition)
                    throw new MotionInterlockException("Teach the selected PCB pickup XYZ before moving Supply.");
                await MoveAxisAsync(MotionAxis.Z, _settings.RotationZ, cancellationToken);
                await _motion.MoveToXYAsync(
                    position.X,
                    position.Y,
                    _settings.Motion.HorizontalSpeed,
                    cancellationToken);
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                break;
            case TeachMode.Full or TeachMode.XYOnly:
                if (Rotation != PcbSupplyRotationState.Unrotated)
                    throw new MotionInterlockException("Supply must be unrotated before moving to the handoff position.");
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                await _motion.MoveToXYAsync(
                    position.X, position.Y, _settings.Motion.HorizontalSpeed, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    internal async Task MoveFromHandoffAsync(PcbPickPosition nextPick, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Rotation != PcbSupplyRotationState.Unrotated)
            throw new MotionInterlockException("Supply must remain Unrotated until it leaves the handoff position.");
        if (nextPick.Y is not { } y)
            throw new MotionInterlockException("Teach the selected PCB pickup XYZ before moving Supply.");
        await MoveAxisAsync(MotionAxis.Z, _settings.HandoffPosition.Z, cancellationToken);
        await _motion.MoveToXYAsync(
            nextPick.X,
            y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    public async Task<bool> HomeAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        return await _motion.HomeAsync(axis, _settings.Motion.Home(axis).SearchSpeed, cancellationToken);
    }

    public async Task<bool> HomeHorizontalAsync(CancellationToken cancellationToken = default)
    {
        return await _motion.HomeHorizontalAsync(
            _settings.Motion.HorizontalHome.SearchSpeed,
            cancellationToken);
    }

    public Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        CancellationToken cancellationToken = default)
    {
        var speed = axis == MotionAxis.Z ? _settings.Motion.ZSpeed : _settings.Motion.HorizontalSpeed;
        cancellationToken.ThrowIfCancellationRequested();
        if (axis == MotionAxis.Z && !_motion.GetAxisState(axis).Homed)
            throw new MotionInterlockException("Home Supply Z before moving to a taught height.");
        return _motion.MoveAxisAsync(axis, position, speed, cancellationToken);
    }

    public Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        return _motion.JogAsync(axis, velocity, cancellationToken);
    }

    public Task AdjustAxisAsync(
        MotionAxis axis, double position, double velocity, CancellationToken cancellationToken = default)
    {
        return _motion.AdjustAxisAsync(axis, position, velocity, cancellationToken);
    }

    public async Task SetRotatedAsync(bool rotated, CancellationToken cancellationToken = default)
    {
        await MoveAxisAsync(MotionAxis.Z, _settings.RotationZ, cancellationToken);
        await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, rotated, cancellationToken);
    }
}
