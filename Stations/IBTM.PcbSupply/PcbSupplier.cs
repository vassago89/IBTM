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
    private const double TeachingRotationPositionToleranceMillimeters = 0.05;

    private readonly IXyMotion _motion;
    private readonly PcbSupplySettings _settings;
    private readonly UnitSettings _units;
    private readonly RecipeManager _recipes;
    // Pickup scanning always starts from PCB 1 on START.
    private PickStep _pickStep;
    // Commissioning input, kept only for this application session.
    private volatile bool _testUpstreamCarrierAvailable;

    public PcbSupplier(
        IXyMotion motion,
        MotionStatus motionStatus,
        IIoService io,
        PcbSupplySettings settings,
        RecipeManager recipes,
        UnitSettings units)
        : base(io, [
            InputIo.AutoMode,
            InputIo.PcbSupplyAvailableFromFront1,
            InputIo.PcbSupplyUnrotated,
            InputIo.PcbSupplyRotated,
            InputIo.PcbSupplyGripperClosed,
            InputIo.PcbSupplyGripperOpen,
            InputIo.PcbSupplyIpmFixerForward,
            InputIo.PcbSupplyPcbDetected,
        ])
    {
        _motion = motion;
        _settings = settings;
        _units = units;
        _recipes = recipes;
        Motion = motionStatus;
        Phase = PcbSupplyState.MovingToPickup;
        motion.StateChanged += NotifyChanged;
        StepChanged += NotifyChanged;
    }

    public MotionStatus Motion { get; }

    public bool UpstreamCarrierAvailable
    {
        get
        {
            return Io.GetInput(InputIo.AutoMode)
                ? _testUpstreamCarrierAvailable
                : Io.GetInput(InputIo.PcbSupplyAvailableFromFront1);
        }
    }

    public bool TestUpstreamCarrierAvailable
    {
        get => _testUpstreamCarrierAvailable;
        set
        {
            // The selector contact is ON in teaching/manual mode.
            value = value && Io.IsReady && Io.GetInput(InputIo.AutoMode);
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
            switch ((Io.GetInput(InputIo.PcbSupplyGripperOpen), Io.GetInput(InputIo.PcbSupplyGripperClosed)))
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
            if (!Io.GetInput(InputIo.PcbSupplyPcbDetected))
            {
                return PcbSupplyPcbState.None;
            }

            return Gripper == PcbSupplyCylinderState.Forward
                && Io.GetInput(InputIo.PcbSupplyIpmFixerForward)
                ? PcbSupplyPcbState.Secured
                : PcbSupplyPcbState.Detected;
        }
    }

    public PcbSupplyRotationState Rotation
    {
        get
        {
            switch ((Io.GetInput(InputIo.PcbSupplyUnrotated), Io.GetInput(InputIo.PcbSupplyRotated)))
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
                && !Io.GetInput(InputIo.PcbSupplyIpmFixerForward);
        }
    }

    public void SetUpstreamReady(bool ready, CancellationToken cancellationToken = default)
    {
        if (!Io.GetInput(InputIo.AutoMode))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Io.SetOutput(OutputIo.PcbSupplyReadyToFront1, ready);
        }
    }

    public void StopUpstream()
    {
        // Ready OFF releases the upstream carrier, even in manual mode.
        // STOP must use the physical input, never the teaching test signal.
        if (!Io.IsReady)
            throw new IOException("Supply SMEMA feedback is unavailable; Ready cannot be cleared safely.");
        if (!Io.GetInput(InputIo.PcbSupplyAvailableFromFront1))
            Io.SetOutput(OutputIo.PcbSupplyReadyToFront1, false);
    }

    protected override void OnInputChanged(InputIo input, bool value)
    {
        if (input == InputIo.AutoMode && !value)
            _testUpstreamCarrierAvailable = false;
        OnHandlerChanged();
    }

    private void OnHandlerChanged()
    {
        if (!_units.PcbSupply || _pickStep == PickStep.Pcb1)
            return;

        switch (Phase)
        {
            case PcbSupplyState.MovingToPickup or PcbSupplyState.WaitingForCarrier or PcbSupplyState.WaitingForCarrierExit:
            // The empty carrier can leave while its last PCB is still being handed off.
            case PcbSupplyState.MovingToHandoff or PcbSupplyState.HandingOff or PcbSupplyState.WaitingForPlacementClear
                when _pickStep == PickStep.WaitingForCarrierExit:
                if (!UpstreamCarrierAvailable)
                    _pickStep = PickStep.Pcb1;
                break;
        }
    }

    public PcbSupplyHandoff Handoff
    {
        get
        {
            if (!Io.IsReady || !MotionServiceBase.IsReadyAndStopped(_motion))
                return PcbSupplyHandoff.Unavailable;
            if (!_units.PcbSupply || Rotation != PcbSupplyRotationState.Unrotated
                || Phase is not (PcbSupplyState.HandingOff or PcbSupplyState.WaitingForPlacementClear))
                return PcbSupplyHandoff.Unavailable;
            return PcbSecured ? PcbSupplyHandoff.Holding
                : PcbReleased ? PcbSupplyHandoff.Released : PcbSupplyHandoff.Unavailable;
        }
    }

    public bool IsHandoffRestartAllowed => Phase == PcbSupplyState.HandingOff
        && Handoff == PcbSupplyHandoff.Holding;

    // START is fresh except for a secured PCB already waiting for Placement.
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

    public async Task RunAsync(
        IPcbPlacementHandoff placement,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        var continueHandoff = IsHandoffRestartAllowed;
        if (_units.PcbSupply && Pcb != PcbSupplyPcbState.None
            && Gripper != PcbSupplyCylinderState.Backward && !continueHandoff)
            throw new InvalidOperationException("Remove the Supply PCB before starting a new run.");
        Exception? failure = null;
        _pickStep = PickStep.Pcb1;
        // A retained PCB follows the normal forward move before Placement may approach.
        Phase = continueHandoff ? PcbSupplyState.MovingToHandoff : PcbSupplyState.MovingToPickup;
        try
        {
            BeginRun(_units.PcbSupply ? Phase : PcbSupplyState.Disabled);
            placement.Changed += WakeRun;
            // An open gripper can see a PCB below. Retract its fixer before empty travel.
            if (_units.PcbSupply && !continueHandoff
                && Gripper == PcbSupplyCylinderState.Backward
                && Io.GetInput(InputIo.PcbSupplyIpmFixerForward))
                await Io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                // Pickup checks its support below; other partial grips require a live handoff.
                var phase = _units.PcbSupply ? Phase : PcbSupplyState.Disabled;
                if (Pcb == PcbSupplyPcbState.Detected && !PcbReleased)
                {
                    switch (phase)
                    {
                        case PcbSupplyState.PickingPcb:
                        case PcbSupplyState.HandingOff when placement.Handoff == PcbPlacementHandoff.Holding:
                            break;
                        default:
                            throw new InvalidOperationException(
                                "Supply PCB grip is incomplete away from a confirmed support. Check gripper and IPM fixation before moving or releasing it.");
                    }
                }
                var step = GetNextStep(placement);
                cancellationToken.ThrowIfCancellationRequested();
                var recipe = _recipes.Current.PcbSupply;
                if (step == PcbSupplyState.Disabled)
                {
                    EnterStep(step);
                    await WaitForChangeAsync(cancellationToken);
                    continue;
                }
                if (Phase is PcbSupplyState.HandingOff or PcbSupplyState.WaitingForPlacementClear
                    && Rotation != PcbSupplyRotationState.Unrotated)
                    throw new MotionInterlockException("Supply handoff requires confirmed Unrotated feedback.");

                if (_pickStep != PickStep.WaitingForCarrierExit)
                {
                    SetUpstreamReady(true, cancellationToken);
                }
                else if (Phase is PcbSupplyState.HandingOff or PcbSupplyState.WaitingForCarrierExit)
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
                    case PcbSupplyState.MovingToPickup:
                        var nextPick = _pickStep == PickStep.Pcb2 ? recipe.Pcb2PickPosition : recipe.Pcb1PickPosition;
                        await MoveFromHandoffAsync(nextPick, cancellationToken);
                        EnterStep(_pickStep == PickStep.WaitingForCarrierExit
                            ? PcbSupplyState.WaitingForCarrierExit : PcbSupplyState.WaitingForCarrier);
                        break;
                    case PcbSupplyState.PickingPcb:
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
                            if (!UpstreamCarrierAvailable)
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
                            await Io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false, pickup.Token);
                            await Io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false, pickup.Token);
                            EnterStep(PcbSupplyState.MovingToPickup);
                            await MoveToPickupAsync(pickPosition, pickup.Token);
                            await _motion.MoveAxisAsync(MotionAxis.Z, pickPosition.Z, _settings.Motion.ZSpeed, pickup.Token);
                            pickup.Token.ThrowIfCancellationRequested();
                            EnterStep(step);
                            if (Pcb == PcbSupplyPcbState.None)
                            {
                                await MoveAxisAsync(MotionAxis.Z, _settings.TravelZ, pickup.Token);
                                EnterStep(PcbSupplyState.WaitingForCarrier);
                            }
                            else
                            {
                                // Presence can be ON before reaching the PCB; grip only at the taught pickup XYZ.
                                await Io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true, pickup.Token);
                                await Io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true, pickup.Token);
                                holdingRequired = true;
                                CheckPickupFeedback();
                                pickup.Token.ThrowIfCancellationRequested();
                                EnterStep(PcbSupplyState.MovingToHandoff);
                            }

                            pickup.Token.ThrowIfCancellationRequested();
                            // Never advance a replacement carrier.
                            if (!carrierChanged)
                            {
                                _pickStep = pickStep == PickStep.Pcb1 ? PickStep.Pcb2 : PickStep.WaitingForCarrierExit;
                                if (!PcbSecured && _pickStep == PickStep.WaitingForCarrierExit)
                                    EnterStep(PcbSupplyState.WaitingForCarrierExit);
                            }
                        }
                        catch (OperationCanceledException) when (carrierChanged
                            && !cancellationToken.IsCancellationRequested)
                        {
                            if (!PcbReleased)
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
                        using var carrying = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        void CheckHolding()
                        {
                            if (!PcbSecured)
                                OperationCancellation.CancelIfNotDisposed(carrying);
                        }
                        Changed += CheckHolding;
                        try
                        {
                            CheckHolding();
                            carrying.Token.ThrowIfCancellationRequested();
                            await PrepareHandoffAsync(carrying.Token);
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
                        if (!MotionServiceBase.IsReadyAndStopped(_motion)
                            || Rotation != PcbSupplyRotationState.Unrotated)
                            throw new MotionInterlockException("Supply axes must be ready and stopped with Unrotated feedback before releasing the PCB.");
                        if (PcbSecured && placement.Handoff != PcbPlacementHandoff.Holding)
                        {
                            await WaitForChangeAsync(cancellationToken);
                            continue;
                        }
                        if (Io.GetInput(InputIo.PcbSupplyIpmFixerForward))
                        {
                            if (placement.Handoff != PcbPlacementHandoff.Holding)
                                throw new InvalidOperationException("Placement must detect and secure the PCB before supply releases its fixer.");
                            await Io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false, cancellationToken);
                        }
                        if (Gripper != PcbSupplyCylinderState.Backward)
                        {
                            if (!MotionServiceBase.IsReadyAndStopped(_motion)
                                || Rotation != PcbSupplyRotationState.Unrotated)
                                throw new MotionInterlockException("Supply lost axis readiness or Unrotated feedback before opening its gripper.");
                            if (placement.Handoff != PcbPlacementHandoff.Holding)
                                throw new InvalidOperationException("Placement lost PCB holding feedback before supply opened its gripper.");
                            await Io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false, cancellationToken);
                        }
                        EnterStep(PcbSupplyState.WaitingForPlacementClear);
                        break;
                    case PcbSupplyState.WaitingForCarrier or PcbSupplyState.WaitingForCarrierExit
                        or PcbSupplyState.WaitingForPlacementClear:
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

    internal PcbSupplyState GetNextStep(IPcbPlacementHandoff placement)
    {
        if (!_units.PcbSupply)
            return PcbSupplyState.Disabled;
        var state = Phase;
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
        EnterStep(PcbSupplyState.MovingToHandoff);
        await MoveToPositionAsync(position, Rotation, cancellationToken);
        if (Rotation != PcbSupplyRotationState.Unrotated)
            await Io.SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
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
            await Io.SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, true, cancellationToken);
        }
        await MoveToPickupAsync(nextPick, cancellationToken);
    }

    private async Task MoveToPositionAsync(
        AxisPosition position,
        PcbSupplyRotationState rotation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

    public bool IsTeachingRotationAllowed => TeachingRotationBlockReason is null;

    private string? TeachingRotationBlockReason
    {
        get
        {
            if (!Io.IsReady)
                return UiText.Get("Supply rotation blocked: I/O is unavailable.");
            if (!_motion.IsReady)
                return UiText.Get("Supply rotation blocked: motion feedback is unavailable.");
            if (_motion.IsMoving)
                return UiText.Get("Supply rotation blocked: axes are moving.");
            foreach (var axis in _motion.Axes)
            {
                var state = _motion.GetAxisState(axis);
                if (state is not { Homed: true, ServoOn: true, Alarm: false, Emergency: false, InMotion: false, InPosition: true })
                    return UiText.Format($"Supply rotation blocked: {axis} axis is not ready. {state}");
            }
            var current = _motion.Position;
            var handoff = _settings.HandoffPosition;
            if (Math.Abs(current.X - handoff.X) <= TeachingRotationPositionToleranceMillimeters
                && (!_motion.HasY || Math.Abs(current.Y - handoff.Y) <= TeachingRotationPositionToleranceMillimeters)
                && (!_motion.HasZ || Math.Abs(current.Z - handoff.Z) <= TeachingRotationPositionToleranceMillimeters))
                return null;
            return UiText.Format($"Supply rotation blocked: current XYZ=({current.X:F3}, {current.Y:F3}, {current.Z:F3}), handoff XYZ=({handoff.X:F3}, {handoff.Y:F3}, {handoff.Z:F3}), tolerance={TeachingRotationPositionToleranceMillimeters:F3} mm.");
        }
    }

    public async Task SetTeachingRotationAsync(bool rotated, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var rotation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string? blockedReason = null;
        void CheckPosition()
        {
            if (rotation.IsCancellationRequested || TeachingRotationBlockReason is not { } reason)
                return;
            // Preserve the first rejected feedback, even if it recovers before the await resumes.
            Interlocked.CompareExchange(ref blockedReason, reason, null);
            OperationCancellation.CancelIfNotDisposed(rotation);
        }
        Changed += CheckPosition;
        try
        {
            CheckPosition();
            rotation.Token.ThrowIfCancellationRequested();
            await Io.SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, rotated, rotation.Token);
            CheckPosition();
            rotation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MotionInterlockException(blockedReason ?? "Supply must remain at handoff XYZ while rotating.");
        }
        finally
        {
            Changed -= CheckPosition;
        }
    }
}
