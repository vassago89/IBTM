using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed partial class PcbSupplier : AutoUnit, IPcbSupplyHandoff
{
    private readonly UnitSettings _units;
    // Slot progress belongs only to the current run and upstream carrier.
    private PickStep _pickStep;

    private readonly IXyMotion _motion;
    private readonly IIoService _io;
    private readonly PcbSupplySettings _settings;
    // Commissioning input, kept only for this application session.
    private volatile bool _testUpstreamCarrierAvailable;

    public PcbSupplier(IXyMotion motion, IIoService io, PcbSupplySettings settings, UnitSettings units)
    {
        _motion = motion;
        _io = io;
        _settings = settings;
        _units = units;
        Motion = new(motion);
        io.InputChanged += OnInputChanged;
        motion.StateChanged += NotifyChanged;
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
            Changed?.Invoke();
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

    public bool IpmFixed => _io.GetInput(InputIo.PcbSupplyIpmFixerForward);

    public PcbSupplyPcbState Pcb
    {
        get
        {
            if (!_io.GetInput(InputIo.PcbSupplyPcbDetected))
            {
                return PcbSupplyPcbState.None;
            }

            return Gripper == PcbSupplyCylinderState.Forward
                && IpmFixed
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
                && !IpmFixed;
        }
    }

    public bool IsAtRotationZ(bool live = true)
    {
        return live ? _motion.IsAtHorizontalZ : Motion.IsAtZ(_settings.RotationZ);
    }

    public bool IsAtPickupXY(PcbPickPosition position)
    {
        if (position.Y is not { } y)
            return false;
        var current = _motion.GetPosition();
        return Motion.IsSettled(true, MotionAxis.X, MotionAxis.Y)
            && Math.Abs(current.X - position.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - y) <= MotionService.PositionToleranceMillimeters;
    }

    public bool IsAtPickup(PcbPickPosition position)
    {
        return position.Y is { } y
            && Motion.IsAt(new() { X = position.X, Y = y, Z = position.Z }, live: true);
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

    public void SetUpstreamReady(bool ready)
    {
        _io.SetAutomaticSmemaOutput(OutputIo.PcbSupplyReadyToFront1, ready);
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

    public async Task MoveToHandoffAsync(CancellationToken cancellationToken, AxisPosition? position = null)
    {
        if (Rotation != PcbSupplyRotationState.Unrotated)
            throw new MotionInterlockException("Supply must be unrotated before moving to the handoff position.");
        position ??= _settings.HandoffPosition;
        await _motion.MoveToHorizontalZAsync(cancellationToken, travelZ: position.Z);
        await _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    internal async Task PickAsync(
        PcbPickPosition position,
        CancellationToken cancellationToken = default)
    {
        if (!IsAtPickup(position))
        {
            await SetIpmFixerAsync(false, cancellationToken);
            await SetGripperClosedAsync(false, cancellationToken);
            await MoveToPickupAsync(position, cancellationToken);
            await _motion.MoveAxisAsync(MotionAxis.Z, position.Z, _settings.Motion.ZSpeed, cancellationToken);
        }
        if (Pcb == PcbSupplyPcbState.None)
        {
            await _motion.MoveToHorizontalZAsync(cancellationToken);
            return;
        }
        // Presence can be ON before reaching the PCB; grip only at the taught pickup XYZ.
        await SetGripperClosedAsync(true, cancellationToken);
        await SetIpmFixerAsync(true, cancellationToken);
    }

    internal async Task MoveToPickupAsync(PcbPickPosition position, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (position.Y is not { } y)
            throw new MotionInterlockException("Teach the selected PCB pickup XYZ before moving Supply.");
        await _motion.MoveToHorizontalZAsync(cancellationToken);
        await _motion.MoveToXYAsync(
            position.X,
            y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
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
                await _motion.MoveToHorizontalZAsync(cancellationToken);
                await _motion.MoveToXYAsync(
                    position.X,
                    position.Y,
                    _settings.Motion.HorizontalSpeed,
                    cancellationToken);
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                break;
            case TeachMode.Full or TeachMode.XYOnly:
                await MoveToHandoffAsync(cancellationToken, position);
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
        await _motion.MoveToHorizontalZAsync(cancellationToken, travelZ: _settings.HandoffPosition.Z);
        await _motion.MoveToXYAsync(
            nextPick.X,
            y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    public Task SetIpmFixerAsync(bool forward, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, forward, cancellationToken);
    }

    public Task SetGripperClosedAsync(bool closed, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, closed, cancellationToken);
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

    public Task MoveToRotationZAsync(CancellationToken cancellationToken = default)
    {
        return _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    public async Task SetRotatedAsync(bool rotated, CancellationToken cancellationToken = default)
    {
        await _motion.MoveToHorizontalZAsync(cancellationToken);
        await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, rotated, cancellationToken);
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
            Changed?.Invoke();
        }
    }

    public async Task RunAsync(
        PcbSupplyRecipe recipe,
        IPcbPlacementHandoff placement,
        CancellationToken cancellationToken = default,
        bool repeat = false)
    {
        Exception? failure = null;
        try
        {
            BeginRun();
            placement.Changed += OnChanged;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // A partial grip can resume only where the slot or the other handler supports the PCB.
                    if (!PcbSecured && !PcbReleased
                        && !(Rotation == PcbSupplyRotationState.Rotated
                            && UpstreamCarrierAvailable
                            && (IsAtPickup(recipe.Pcb1PickPosition)
                                || IsAtPickup(recipe.Pcb2PickPosition)))
                        && !(IsAtHandoff()
                            && placement.Handoff is PcbPlacementHandoff.Holding or PcbPlacementHandoff.Returning))
                    {
                        throw new InvalidOperationException(
                            "Supply PCB grip is incomplete away from a confirmed support. Check gripper and IPM fixation before moving or releasing it.");
                    }
                    if (repeat)
                        await ExecuteRepeatAsync(recipe, placement, cancellationToken);
                    else
                        await ExecuteAsync(recipe, placement, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                placement.Changed -= OnChanged;
                EndRun(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _pickStep = PickStep.Pcb1;
            _repeatState = null;
            try
            {
                StopUpstream();
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
        }
    }

    private async Task ExecuteAsync(
        PcbSupplyRecipe recipe,
        IPcbPlacementHandoff placement,
        CancellationToken cancellationToken)
    {
        if (IsAtHandoff() && Rotation != PcbSupplyRotationState.Unrotated)
            throw new MotionInterlockException("Supply handoff requires confirmed Unrotated feedback.");

        if (_pickStep != PickStep.WaitingForCarrierExit)
        {
            SetUpstreamReady(true);
        }
        else if (IsAtRotationZ() && Pcb != PcbSupplyPcbState.Detected)
        {
            // Keep Ready through both slot checks and the final pickup lift.
            // Its falling edge tells the upstream machine that pickup is complete.
            SetUpstreamReady(false);
        }

        var state = State;
        switch (state)
        {
            case PcbSupplyState.WaitingForPlacement when placement.Handoff == PcbPlacementHandoff.Holding:
                state = PcbSupplyState.ReleasingPcb;
                break;
            case PcbSupplyState.WaitingForPlacementZ when placement.Handoff == PcbPlacementHandoff.Clear:
                state = PcbSupplyState.MovingToPickup;
                break;
        }
        if (state == PcbSupplyState.WaitingForCarrier && !IsAtPickupXY(recipe.Pcb1PickPosition))
            state = PcbSupplyState.MovingToPickup;
        // Resume an interrupted grip at the actual slot without lifting an unsecured PCB.
        if (!IsAtHandoff() && Pcb == PcbSupplyPcbState.Detected
            && Rotation == PcbSupplyRotationState.Rotated && UpstreamCarrierAvailable)
        {
            if (IsAtPickup(recipe.Pcb1PickPosition))
            {
                _pickStep = PickStep.Pcb1;
                state = PcbSupplyState.PickingPcb;
            }
            else if (IsAtPickup(recipe.Pcb2PickPosition))
            {
                _pickStep = PickStep.Pcb2;
                state = PcbSupplyState.PickingPcb;
            }
        }
        TraceStep(state, _pickStep.ToString());
        switch (state)
        {
            case PcbSupplyState.MovingToPickup:
                var nextPick = _pickStep == PickStep.Pcb2 ? recipe.Pcb2PickPosition : recipe.Pcb1PickPosition;
                if (IsAtHandoff())
                    await MoveFromHandoffAsync(nextPick, cancellationToken);
                await SetRotatedAsync(true, cancellationToken);
                if (!IsAtPickupXY(nextPick))
                    await MoveToPickupAsync(nextPick, cancellationToken);
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
                    await PickAsync(pickPosition, pickup.Token);
                    // Never advance a replacement carrier.
                    if (!carrierChanged)
                        _pickStep = pickStep == PickStep.Pcb1 ? PickStep.Pcb2 : PickStep.WaitingForCarrierExit;
                }
                catch (OperationCanceledException) when (carrierChanged
                    && !cancellationToken.IsCancellationRequested)
                {
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
                    await MoveToHandoffAsync(handoff.Token);
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
            case PcbSupplyState.ReleasingPcb:
                await ReleasePcbAsync(placement, cancellationToken);
                break;
            default:
                await WaitForChangeAsync(cancellationToken);
                break;
        }
    }

    private async Task ReleasePcbAsync(
        IPcbPlacementHandoff placement,
        CancellationToken cancellationToken)
    {
        if (Rotation != PcbSupplyRotationState.Unrotated)
            throw new MotionInterlockException("Supply must remain Unrotated while releasing the PCB.");
        if (IpmFixed)
        {
            if (placement.Handoff != PcbPlacementHandoff.Holding)
                throw new InvalidOperationException("Placement must detect and secure the PCB before supply releases its fixer.");
            await SetIpmFixerAsync(false, cancellationToken);
        }
        if (Gripper != PcbSupplyCylinderState.Backward)
        {
            if (Rotation != PcbSupplyRotationState.Unrotated)
                throw new MotionInterlockException("Supply lost Unrotated feedback before opening its gripper.");
            if (placement.Handoff != PcbPlacementHandoff.Holding)
                throw new InvalidOperationException("Placement lost PCB holding feedback before supply opened its gripper.");
            await SetGripperClosedAsync(false, cancellationToken);
        }
    }

    private void OnHandlerChanged()
    {
        if (_repeatState is null && _pickStep != PickStep.Pcb1
            && !UpstreamCarrierAvailable)
        {
            _pickStep = PickStep.Pcb1;
        }
    }

    public PcbSupplyHandoff Handoff
    {
        get
        {
            if (!_units.PcbSupply || Rotation != PcbSupplyRotationState.Unrotated || !IsAtHandoff())
                return PcbSupplyHandoff.Unavailable;
            return PcbSecured ? PcbSupplyHandoff.Holding
                : PcbReleased ? PcbSupplyHandoff.Released : PcbSupplyHandoff.Unavailable;
        }
    }

    public PcbSupplyState State
    {
        get
        {
            if (!_units.PcbSupply)
                return PcbSupplyState.Disabled;
            if (_repeatState is { } repeatState)
                return repeatState;
            var pcb = Pcb;
            var rotation = Rotation;
            var atHandoff = IsAtHandoff();

            switch (true)
            {
                case true when atHandoff && PcbReleased:
                    return PcbSupplyState.WaitingForPlacementZ;
                case true when atHandoff && pcb == PcbSupplyPcbState.Secured:
                    return PcbSupplyState.WaitingForPlacement;
                case true when atHandoff:
                    return PcbSupplyState.ReleasingPcb;
            }

            switch (true)
            {
                case true when pcb == PcbSupplyPcbState.Secured:
                    return PcbSupplyState.MovingToHandoff;
                case true when !IsAtRotationZ() || rotation != PcbSupplyRotationState.Rotated:
                    return PcbSupplyState.MovingToPickup;
                case true when _pickStep == PickStep.WaitingForCarrierExit:
                    return PcbSupplyState.WaitingForCarrierExit;
                default:
                    return UpstreamCarrierAvailable
                        ? PcbSupplyState.PickingPcb
                        : PcbSupplyState.WaitingForCarrier;
            }
        }
    }

    private enum PickStep
    {
        Pcb1,
        Pcb2,
        WaitingForCarrierExit,
    }
}
