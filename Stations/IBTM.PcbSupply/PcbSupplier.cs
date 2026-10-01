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
    // Slot progress belongs only to the current run.
    private PickStep _pickStep;
    private bool _repeat;
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
        if (_handoffPosition is { } position && !MotionServiceBase.IsHoldingPosition(_motion, position))
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

    public void SetUpstreamReady(bool ready, CancellationToken cancellationToken = default)
    {
        if (!_io.GetInput(InputIo.AutoMode))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _io.SetOutput(OutputIo.PcbSupplyReadyToFront1, ready);
        }
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
            && (Phase is PcbSupplyState.MovingToPickup or PcbSupplyState.WaitingForCarrier or PcbSupplyState.WaitingForCarrierExit
                || _pickStep == PickStep.WaitingForCarrierExit
                && Phase is PcbSupplyState.MovingToHandoff or PcbSupplyState.HandingOff or PcbSupplyState.WaitingForPlacementClear)
            && !UpstreamCarrierAvailable)
        {
            // The empty carrier can leave while its last PCB is still being handed off.
            _pickStep = PickStep.Pcb1;
        }
    }

    public PcbSupplyHandoff Handoff
    {
        get
        {
            var position = _handoffPosition;
            if (position is null || !MotionServiceBase.IsHoldingPosition(_motion, position))
                return PcbSupplyHandoff.Unavailable;
            if (!_units.PcbSupply || Rotation != PcbSupplyRotationState.Unrotated
                || Phase is not (PcbSupplyState.HandingOff or PcbSupplyState.WaitingForPlacementClear
                    or PcbSupplyState.WaitingForReturnedPcbGrip or PcbSupplyState.WaitingForReturnClear))
                return PcbSupplyHandoff.Unavailable;
            return PcbSecured ? PcbSupplyHandoff.Holding
                : PcbReleased ? PcbSupplyHandoff.Released : PcbSupplyHandoff.Unavailable;
        }
    }

    // Current run phase; START always selects a fresh initial phase.
    public PcbSupplyState Phase { get; private set; }

    private void EnterStep(PcbSupplyState step, string? target = null)
    {
        var phaseChanged = step != PcbSupplyState.Disabled && Phase != step;
        if (phaseChanged)
            Phase = step;
        base.EnterStep(step, target ?? _pickStep.ToString());
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
        if (cancellationToken.IsCancellationRequested)
            return;
        if (_units.PcbSupply && Pcb != PcbSupplyPcbState.None)
            throw new InvalidOperationException("Remove the Supply PCB before starting a new run.");
        Exception? failure = null;
        _repeat = repeat;
        _pickStep = PickStep.Pcb1;
        _handoffPosition = null;
        Phase = PcbSupplyState.MovingToPickup;
        try
        {
            BeginRun(_units.PcbSupply ? Phase : PcbSupplyState.Disabled);
            placement.Changed += WakeRun;
            while (!cancellationToken.IsCancellationRequested)
            {
                // Pickup and return release check their support below; other partial grips require a live handoff.
                var phase = _units.PcbSupply ? Phase : PcbSupplyState.Disabled;
                if (Pcb == PcbSupplyPcbState.Detected && !PcbReleased
                    && phase is not (PcbSupplyState.PickingPcb or PcbSupplyState.PlacingReturnedPcb or PcbSupplyState.PickingReturnedPcb)
                    && !(phase == PcbSupplyState.ReceivingReturnedPcb
                        && placement.Handoff == PcbPlacementHandoff.Returning)
                    && !(phase == PcbSupplyState.HandingOff
                        && placement.Handoff is PcbPlacementHandoff.Holding or PcbPlacementHandoff.Returning))
                {
                    throw new InvalidOperationException(
                        "Supply PCB grip is incomplete away from a confirmed support. Check gripper and IPM fixation before moving or releasing it.");
                }
                var step = GetNextStep(placement, repeat);
                cancellationToken.ThrowIfCancellationRequested();
                var recipe = _recipes.Current.PcbSupply;
                if (step == PcbSupplyState.Disabled)
                {
                    EnterStep(step);
                    await WaitForChangeAsync(cancellationToken);
                    continue;
                }
                if (!repeat && Phase is PcbSupplyState.HandingOff or PcbSupplyState.WaitingForPlacementClear
                    && Rotation != PcbSupplyRotationState.Unrotated)
                    throw new MotionInterlockException("Supply handoff requires confirmed Unrotated feedback.");

                if ((!repeat || !_units.PcbPlacement) && _pickStep != PickStep.WaitingForCarrierExit)
                {
                    SetUpstreamReady(true, cancellationToken);
                }
                else if (!repeat && Phase is PcbSupplyState.HandingOff or PcbSupplyState.WaitingForCarrierExit)
                {
                    // Keep Ready through both slot checks and the final pickup lift.
                    // Its falling edge tells the upstream machine that pickup is complete.
                    SetUpstreamReady(false, cancellationToken);
                }

                if (Phase == PcbSupplyState.WaitingForCarrierExit && step == PcbSupplyState.MovingToPickup)
                    _pickStep = PickStep.Pcb1;
                EnterStep(step, _pickStep.ToString());
                switch (step)
                {
                    case PcbSupplyState.PreparingReturnReceipt:
                        if (placement.ReturningPcb is not { } returnedPcb)
                        {
                            await WaitForChangeAsync(cancellationToken);
                            continue;
                        }
                        _pickStep = returnedPcb == HeatSinkSlot.HeatSink2 ? PickStep.Pcb2 : PickStep.Pcb1;
                        if (Rotation != PcbSupplyRotationState.Unrotated
                            && placement.Handoff is PcbPlacementHandoff.Returning or PcbPlacementHandoff.Holding)
                            throw new MotionInterlockException("Supply cannot prepare rotation while Placement holds the PCB at receive Z.");
                        if (!PcbSecured)
                        {
                            await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false, cancellationToken);
                            await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false, cancellationToken);
                        }
                        if (_handoffPosition is not { } handoffPosition
                            || !MotionServiceBase.IsHoldingPosition(_motion, handoffPosition)
                            || Rotation != PcbSupplyRotationState.Unrotated)
                        {
                            await PrepareHandoffAsync(cancellationToken);
                        }
                        EnterStep(PcbSupplyState.WaitingForReturnedPcbGrip);
                        break;
                    case PcbSupplyState.ReceivingReturnedPcb:
                        if (placement.Handoff != PcbPlacementHandoff.Returning || Pcb == PcbSupplyPcbState.None)
                            throw new InvalidOperationException("Supply must detect the returned PCB supported by placement before gripping it.");
                        if (_handoffPosition is not { } returnHandoff
                            || !MotionServiceBase.IsHoldingPosition(_motion, returnHandoff)
                            || Rotation != PcbSupplyRotationState.Unrotated)
                            throw new MotionInterlockException("Supply must remain at its unrotated handoff position while receiving the returned PCB.");
                        await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true, cancellationToken);
                        if (_handoffPosition is null
                            || !MotionServiceBase.IsHoldingPosition(_motion, returnHandoff)
                            || Rotation != PcbSupplyRotationState.Unrotated)
                            throw new MotionInterlockException("Supply lost its unrotated handoff position before advancing its fixer.");
                        if (placement.Handoff != PcbPlacementHandoff.Returning || Pcb == PcbSupplyPcbState.None)
                            throw new InvalidOperationException("Placement lost PCB holding feedback before supply secured its fixer.");
                        await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true, cancellationToken);
                        EnterStep(PcbSupplyState.WaitingForReturnClear);
                        break;
                    case PcbSupplyState.WaitingForReturnClear:
                        if (!PcbSecured)
                            throw new InvalidOperationException("Supply lost the returned PCB before placement cleared the handoff.");
                        await WaitForChangeAsync(cancellationToken);
                        continue;
                    case PcbSupplyState.ReturningToPickup:
                    {
                        var returnPosition = _pickStep == PickStep.Pcb1 ? recipe.Pcb1PickPosition : recipe.Pcb2PickPosition;
                        using var returning = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        void CheckReturnHolding()
                        {
                            if (!PcbSecured)
                                OperationCancellation.CancelIfNotDisposed(returning);
                        }
                        Changed += CheckReturnHolding;
                        try
                        {
                            CheckReturnHolding();
                            returning.Token.ThrowIfCancellationRequested();
                            await MoveFromHandoffAsync(returnPosition, returning.Token);
                            EnterStep(PcbSupplyState.PlacingReturnedPcb);
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
                    case PcbSupplyState.PlacingReturnedPcb:
                    {
                        var returnPosition = _pickStep == PickStep.Pcb1 ? recipe.Pcb1PickPosition : recipe.Pcb2PickPosition;
                        var support = new AxisPosition
                        {
                            X = returnPosition.X,
                            Y = returnPosition.Y ?? throw new MotionInterlockException("Teach the selected PCB pickup XYZ before moving Supply."),
                            Z = returnPosition.Z,
                        };
                        using var placing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        var releasing = false;
                        void CheckReturnSupport()
                        {
                            if (!releasing && !PcbSecured
                                || Rotation != PcbSupplyRotationState.Rotated
                                || releasing && !MotionServiceBase.IsHoldingPosition(_motion, support))
                                OperationCancellation.CancelIfNotDisposed(placing);
                        }
                        Changed += CheckReturnSupport;
                        try
                        {
                            CheckReturnSupport();
                            placing.Token.ThrowIfCancellationRequested();
                            await MoveToPickupAsync(returnPosition, placing.Token);
                            await MoveAxisAsync(MotionAxis.Z, returnPosition.Z, placing.Token);
                            CheckReturnSupport();
                            placing.Token.ThrowIfCancellationRequested();
                            if (!MotionServiceBase.IsHoldingPosition(_motion, support))
                                throw new MotionInterlockException("Supply must reach pickup XYZ before releasing the returned PCB.");
                            releasing = true;
                            await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false, placing.Token);
                            await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false, placing.Token);
                            placing.Token.ThrowIfCancellationRequested();
                            // Reuse normal pickup, including the empty lift to travel Z before descending again.
                            EnterStep(PcbSupplyState.PickingReturnedPcb);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new MotionInterlockException("Supply return requires confirmed rotation and pickup position. Check PCB holding feedback before release.");
                        }
                        finally
                        {
                            Changed -= CheckReturnSupport;
                        }
                        break;
                    }
                    case PcbSupplyState.MovingToPickup:
                        var nextPick = _pickStep == PickStep.Pcb2 ? recipe.Pcb2PickPosition : recipe.Pcb1PickPosition;
                        await MoveFromHandoffAsync(nextPick, cancellationToken);
                        if (repeat && _units.PcbPlacement)
                            EnterStep(PcbSupplyState.WaitingForReturnedPcb);
                        else
                            EnterStep(_pickStep == PickStep.WaitingForCarrierExit
                                ? PcbSupplyState.WaitingForCarrierExit : PcbSupplyState.WaitingForCarrier);
                        break;
                    case PcbSupplyState.PickingPcb or PcbSupplyState.PickingReturnedPcb:
                    {
                        var pickStep = _pickStep;
                        var pickPosition = pickStep == PickStep.Pcb1
                            ? recipe.Pcb1PickPosition
                            : recipe.Pcb2PickPosition;
                        using var pickup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        var carrierChanged = false;
                        var holdingRequired = false;
                        void CheckPickupFeedback()
                        {
                            if (step == PcbSupplyState.PickingPcb && !UpstreamCarrierAvailable)
                                carrierChanged = true;
                            if (carrierChanged || Rotation != PcbSupplyRotationState.Rotated
                                || holdingRequired && !PcbSecured)
                                OperationCancellation.CancelIfNotDisposed(pickup);
                        }

                        Changed += CheckPickupFeedback;
                        try
                        {
                            CheckPickupFeedback();
                            EnterStep(step, pickStep.ToString());
                            await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false, pickup.Token);
                            await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false, pickup.Token);
                            if (step == PcbSupplyState.PickingPcb)
                                EnterStep(PcbSupplyState.MovingToPickup);
                            await MoveToPickupAsync(pickPosition, pickup.Token);
                            await _motion.MoveAxisAsync(MotionAxis.Z, pickPosition.Z, _settings.Motion.ZSpeed, pickup.Token);
                            pickup.Token.ThrowIfCancellationRequested();
                            EnterStep(step);
                            if (Pcb == PcbSupplyPcbState.None)
                            {
                                if (step == PcbSupplyState.PickingReturnedPcb)
                                    throw new InvalidOperationException("The returned PCB is missing at its original pickup position.");
                                await MoveAxisAsync(MotionAxis.Z, _settings.TravelZ, pickup.Token);
                                EnterStep(PcbSupplyState.WaitingForCarrier);
                            }
                            else
                            {
                                // Presence can be ON before reaching the PCB; grip only at the taught pickup XYZ.
                                await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true, pickup.Token);
                                await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true, pickup.Token);
                                holdingRequired = true;
                                CheckPickupFeedback();
                                pickup.Token.ThrowIfCancellationRequested();
                                EnterStep(PcbSupplyState.MovingToHandoff);
                            }

                            // Never advance a replacement carrier.
                            if (!carrierChanged)
                            {
                                if (repeat)
                                {
                                    if (PcbSecured)
                                        await MoveAxisAsync(MotionAxis.Z, _settings.TravelZ, pickup.Token);
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
                            if (repeat || !PcbReleased)
                                throw new InvalidOperationException("The upstream carrier left while Supply was gripping its PCB. Check the PCB before resuming pickup.");
                            _pickStep = PickStep.Pcb1;
                            EnterStep(PcbSupplyState.WaitingForCarrier);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            if (holdingRequired && !PcbSecured)
                                throw new InvalidOperationException("Supply lost PCB holding feedback during pickup lift.");
                            throw new MotionInterlockException("Supply rotation must remain confirmed Rotated throughout PCB pickup.");
                        }
                        finally
                        {
                            Changed -= CheckPickupFeedback;
                        }
                        break;
                    }
                    case PcbSupplyState.MovingToHandoff:
                    {
                        using var handoff = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        void CheckHolding()
                        {
                            if (!PcbSecured)
                                OperationCancellation.CancelIfNotDisposed(handoff);
                        }
                        Changed += CheckHolding;
                        try
                        {
                            CheckHolding();
                            handoff.Token.ThrowIfCancellationRequested();
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
                        if (_handoffPosition is not { } releaseHandoff
                            || !MotionServiceBase.IsHoldingPosition(_motion, releaseHandoff)
                            || Rotation != PcbSupplyRotationState.Unrotated)
                            throw new MotionInterlockException("Supply must remain at its confirmed unrotated handoff position while releasing the PCB.");
                        if (repeat && !PcbSecured && placement.Handoff != PcbPlacementHandoff.Holding)
                            throw new InvalidOperationException("Supply repeat lost PCB holding feedback during forward handoff.");
                        if (PcbSecured && placement.Handoff != PcbPlacementHandoff.Holding)
                        {
                            await WaitForChangeAsync(cancellationToken);
                            continue;
                        }
                        if (_io.GetInput(InputIo.PcbSupplyIpmFixerForward))
                        {
                            if (placement.Handoff != PcbPlacementHandoff.Holding)
                                throw new InvalidOperationException("Placement must detect and secure the PCB before supply releases its fixer.");
                            await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false, cancellationToken);
                        }
                        if (Gripper != PcbSupplyCylinderState.Backward)
                        {
                            if (_handoffPosition is null
                                || !MotionServiceBase.IsHoldingPosition(_motion, releaseHandoff)
                                || Rotation != PcbSupplyRotationState.Unrotated)
                                throw new MotionInterlockException("Supply lost its unrotated handoff position before opening its gripper.");
                            if (placement.Handoff != PcbPlacementHandoff.Holding)
                                throw new InvalidOperationException("Placement lost PCB holding feedback before supply opened its gripper.");
                            await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false, cancellationToken);
                        }
                        EnterStep(PcbSupplyState.WaitingForPlacementClear);
                        break;
                    case PcbSupplyState.WaitingForCarrier or PcbSupplyState.WaitingForCarrierExit
                        or PcbSupplyState.WaitingForPlacementClear or PcbSupplyState.WaitingForReturnedPcb
                        or PcbSupplyState.WaitingForReturnedPcbGrip:
                        await WaitForChangeAsync(cancellationToken);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(step), step, "Unsupported PCB supply step.");
                }
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
            _repeat = false;
            _handoffPosition = null;
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

    public async Task WaitForRepeatEndAsync(CancellationToken cancellationToken)
    {
        var changed = new AsyncAutoResetEvent();
        Changed += changed.Set;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recipe = _recipes.Current.PcbSupply;
                var pickup = _pickStep == PickStep.Pcb2 ? recipe.Pcb2PickPosition : recipe.Pcb1PickPosition;
                if (Step is PcbSupplyState.WaitingForReturnedPcb && PcbReleased
                    && Rotation == PcbSupplyRotationState.Rotated && pickup.Y is { } y
                    && MotionServiceBase.IsHoldingPosition(_motion,
                        new() { X = pickup.X, Y = y, Z = _settings.TravelZ }))
                    return;
                await changed.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            Changed -= changed.Set;
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
                    or PcbSupplyState.ReturningToPickup or PcbSupplyState.PlacingReturnedPcb
                    or PcbSupplyState.MovingToHandoff:
                    return state;
                case PcbSupplyState.PickingReturnedPcb or PcbSupplyState.MovingToPickup:
                    return PcbSecured ? PcbSupplyState.MovingToHandoff : state;
                case PcbSupplyState.HandingOff:
                    if (!_units.PcbPlacement)
                        return PcbSupplyState.ReturningToPickup;
                    return state;
                case PcbSupplyState.WaitingForPlacementClear:
                    break; // Use normal withdrawal after Placement clears the handoff.
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

    public async Task PrepareHandoffAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var position = _settings.HandoffPosition;
        if (Phase != PcbSupplyState.PreparingReturnReceipt)
            EnterStep(PcbSupplyState.MovingToHandoff);
        await MoveToPositionAsync(position, Rotation, cancellationToken);
        if (Rotation != PcbSupplyRotationState.Unrotated)
            await SetRotatedAsync(false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _handoffPosition = new() { X = position.X, Y = position.Y, Z = position.Z };
        if (Phase != PcbSupplyState.PreparingReturnReceipt)
            EnterStep(PcbSupplyState.HandingOff);
    }

    internal async Task MoveToPickupAsync(PcbPickPosition position, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (position.Y is not { } y)
            throw new MotionInterlockException("Teach the selected PCB pickup XYZ before moving Supply.");
        using var move = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckRotation()
        {
            if (Rotation != PcbSupplyRotationState.Rotated)
                OperationCancellation.CancelIfNotDisposed(move);
        }
        Changed += CheckRotation;
        try
        {
            CheckRotation();
            move.Token.ThrowIfCancellationRequested();
            await MoveAxisAsync(MotionAxis.Z, _settings.TravelZ, move.Token);
            await _motion.MoveToXYAsync(position.X, y, _settings.Motion.HorizontalSpeed, move.Token);
            CheckRotation();
            move.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MotionInterlockException("Supply must remain rotated while moving to a PCB pickup position.");
        }
        finally
        {
            Changed -= CheckRotation;
        }
    }

    public bool IsMoveToTeachingPositionAllowed(TeachingPosition point)
    {
        return point.Target switch
        {
            TeachingTarget.SupplyHandoff => Rotation != PcbSupplyRotationState.Between,
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
            case TeachMode.ZOnly:
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                break;
            case TeachMode.Full when point.Target is TeachingTarget.SupplyPcb1Pick or TeachingTarget.SupplyPcb2Pick:
                if (!point.HasPosition)
                    throw new MotionInterlockException("Teach the selected PCB pickup XYZ before moving Supply.");
                await MoveToPositionAsync(position, PcbSupplyRotationState.Rotated, cancellationToken);
                break;
            case TeachMode.Full or TeachMode.XYOnly:
                await MoveToPositionAsync(position, Rotation, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    internal async Task MoveFromHandoffAsync(PcbPickPosition nextPick, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (nextPick.Y is null)
            throw new MotionInterlockException("Teach the selected PCB pickup XYZ before moving Supply.");
        if (Rotation != PcbSupplyRotationState.Rotated)
        {
            await MoveToPositionAsync(_settings.HandoffPosition, Rotation, cancellationToken);
            await SetRotatedAsync(true, cancellationToken);
        }
        await MoveToPickupAsync(nextPick, cancellationToken);
    }

    private async Task MoveToPositionAsync(
        AxisPosition position,
        PcbSupplyRotationState rotation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MotionServiceBase.IsHoldingPosition(_motion, position) && Rotation == rotation)
            return;
        if (rotation == PcbSupplyRotationState.Between)
            throw new MotionInterlockException("Confirm Supply rotation feedback before moving to the handoff position.");
        using var move = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckRotation()
        {
            if (Rotation != rotation)
                OperationCancellation.CancelIfNotDisposed(move);
        }

        Changed += CheckRotation;
        try
        {
            CheckRotation();
            move.Token.ThrowIfCancellationRequested();
            await MoveAxisAsync(MotionAxis.Z, _settings.TravelZ, move.Token);
            await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, move.Token);
            await MoveAxisAsync(MotionAxis.Z, position.Z, move.Token);
            CheckRotation();
            move.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MotionInterlockException("Supply rotation feedback changed during movement.");
        }
        finally
        {
            Changed -= CheckRotation;
        }
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

    public bool IsRotationAllowed
    {
        get => _io.IsReady && MotionServiceBase.IsHoldingPosition(_motion, _settings.HandoffPosition);
    }

    public async Task SetRotatedAsync(bool rotated, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var rotation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPosition()
        {
            if (!IsRotationAllowed)
                OperationCancellation.CancelIfNotDisposed(rotation);
        }
        Changed += CheckPosition;
        try
        {
            CheckPosition();
            rotation.Token.ThrowIfCancellationRequested();
            await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, rotated, rotation.Token);
            CheckPosition();
            rotation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MotionInterlockException("Supply must remain at handoff XYZ while rotating.");
        }
        finally
        {
            Changed -= CheckPosition;
        }
    }
}
