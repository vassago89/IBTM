using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed partial class PcbSupplier : AutoUnit, IPcbSupplyHandoff
{
    private readonly IXyMotion _motion;
    private readonly IIoService _io;
    private readonly PcbSupplySettings _settings;
    private readonly UnitSettings _units;

    // Slot progress belongs only to the current run and upstream carrier.
    private PickStep _pickStep;
    private bool _repeat;
    private bool _handoffPendingDeparture;
    // Completed stage position, invalidated by motion/state changes; never proof of current readiness.
    private AxisPosition? _handoffPosition;
    // Commissioning input, kept only for this application session.
    private volatile bool _testUpstreamCarrierAvailable;

    public PcbSupplier(IXyMotion motion, MotionStatus motionStatus, IIoService io, PcbSupplySettings settings, UnitSettings units)
    {
        _motion = motion;
        _io = io;
        _settings = settings;
        _units = units;
        Motion = motionStatus;
        io.InputChanged += OnInputChanged;
        motion.StateChanged += OnMotionStateChanged;
        StepChanged += NotifyChanged;
    }

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
        return Motion.IsAtZ(_settings.RotationZ, live);
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
            Changed?.Invoke();
        }
    }

    private void OnHandlerChanged()
    {
        if (!_repeat && _pickStep != PickStep.Pcb1
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
            if (position is null)
                return PcbSupplyHandoff.Unavailable;
            if (!Motion.IsHoldingPosition(position))
            {
                _handoffPosition = null;
                return PcbSupplyHandoff.Unavailable;
            }
            if (!_units.PcbSupply || Rotation != PcbSupplyRotationState.Unrotated
                || State is not (PcbSupplyState.HandingOff or PcbSupplyState.WaitingForPlacementClear
                    or PcbSupplyState.WaitingForReturnedPcbGrip or PcbSupplyState.WaitingForReturnClear))
                return PcbSupplyHandoff.Unavailable;
            return PcbSecured ? PcbSupplyHandoff.Holding
                : PcbReleased ? PcbSupplyHandoff.Released : PcbSupplyHandoff.Unavailable;
        }
    }

    public PcbSupplyState State
    {
        get => !_units.PcbSupply ? PcbSupplyState.Disabled
            : SequenceStep is PcbSupplyState step ? step : PcbSupplyState.MovingToPickup;
        private set
        {
            if (Equals(SequenceStep, value))
                return;
            EnterStep(value, _pickStep.ToString());
            if (!IsRunning)
                Changed?.Invoke();
        }
    }

    private enum PickStep
    {
        Pcb1,
        Pcb2,
        WaitingForCarrierExit,
    }
}
