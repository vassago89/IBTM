using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyHandler
{
    private const double XHome = 0;

    private readonly IAxisMotion _motion;
    private readonly IIoService _io;
    private readonly PcbSupplySettings _settings;
    private readonly PcbBufferSettings _bufferSettings;

    public PcbSupplyHandler(
        IAxisMotion motion,
        IIoService io,
        PcbSupplySettings settings,
        PcbBufferSettings bufferSettings)
    {
        _motion = motion;
        _io = io;
        _settings = settings;
        _bufferSettings = bufferSettings;
        Motion = new(motion);
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

    public MotionStatus Motion { get; }

    public IMotionFeedback Feedback
    {
        get
        {
            return _motion;
        }
    }

    public bool IsAtRotationZ
    {
        get
        {
            return _motion.IsAtHorizontalZ;
        }
    }

    public bool UpstreamCarrierAvailable
    {
        get
        {
            return _io.GetInput(InputIo.PcbSupplyAvailableFromFront1);
        }
    }

    public PcbSupplyCylinderState Gripper
    {
        get
        {
            return CylinderState(InputIo.PcbSupplyGripperClosed, InputIo.PcbSupplyGripperOpen);
        }
    }

    public PcbSupplyCylinderState IpmFixer
    {
        get
        {
            return CylinderState(InputIo.PcbSupplyIpmFixerForward, InputIo.PcbSupplyIpmFixerBackward);
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
                && IpmFixer == PcbSupplyCylinderState.Forward
                ? PcbSupplyPcbState.Secured
                : PcbSupplyPcbState.Detected;
        }
    }

    public PcbSupplyRotationState Rotation
    {
        get
        {
            return (_io.GetInput(InputIo.PcbSupplyUnrotated), _io.GetInput(InputIo.PcbSupplyRotated)) switch
            {
                (true, false) => PcbSupplyRotationState.Unrotated,
                (false, true) => PcbSupplyRotationState.Rotated,
                _ => PcbSupplyRotationState.Between,
            };
        }
    }

    public bool CanPrepareHome
    {
        get
        {
            var rotation = Rotation;
            return rotation != PcbSupplyRotationState.Between
                && (rotation != PcbSupplyRotationState.Unrotated
                    || Pcb == PcbSupplyPcbState.None);
        }
    }

    public void InitializeMotion()
    {
        _motion.Initialize();
    }

    public void ResetMotion()
    {
        _motion.Reset();
    }

    public void SetServo(MotionAxis axis, bool on)
    {
        _motion.SetServo(axis, on);
    }

    internal bool AtHandoffXY
    {
        get
        {
            var position = _motion.GetPosition();
            return !_motion.IsMoving
                && _motion.GetAxisState(MotionAxis.X).InPosition
                && _motion.GetAxisState(MotionAxis.Y).InPosition
                && Math.Abs(position.X - _settings.BufferHandoffPosition.X) <= MotionService.PositionToleranceMillimeters
                && Math.Abs(position.Y - _settings.BufferHandoffPosition.Y) <= MotionService.PositionToleranceMillimeters;
        }
    }

    internal bool InHandoffZRange
    {
        get
        {
            var z = _motion.GetPosition().Z;
            return z > _settings.RotationZ + MotionService.PositionToleranceMillimeters
                && z <= _settings.BufferHandoffPosition.Z + MotionService.PositionToleranceMillimeters;
        }
    }

    public void SetUpstreamReady(bool ready)
    {
        _io.SetOutput(OutputIo.PcbSupplyReadyToFront1, ready);
    }

    internal async Task MoveAboveHandoffAsync(CancellationToken cancellationToken)
    {
        await _motion.MoveToHorizontalZAsync(cancellationToken);
        await MoveHorizontalAsync(
            _settings.BufferHandoffPosition.X,
            _settings.BufferHandoffPosition.Y,
            cancellationToken);
    }

    public Task MoveToHandoffZAsync(CancellationToken cancellationToken, double? z = null)
    {
        return Rotation != PcbSupplyRotationState.Rotated
            ? throw new InvalidOperationException("Supply must be rotated before lowering into the buffer.")
            : _motion.MoveAxisAsync(
                MotionAxis.Z,
                z ?? _settings.BufferHandoffPosition.Z,
                _settings.Motion.ZSpeed,
                cancellationToken);
    }

    internal async Task PickAsync(
        PcbPickPosition position,
        CancellationToken cancellationToken = default)
    {
        await MoveHorizontalAsync(position.X, _settings.CarrierY, cancellationToken);
        await _motion.MoveAxisAsync(MotionAxis.Z, position.Z, _settings.Motion.ZSpeed, cancellationToken);
        if (Pcb == PcbSupplyPcbState.None)
        {
            await _motion.MoveToHorizontalZAsync(cancellationToken);
        }
    }

    public async Task SecurePcbAsync(CancellationToken cancellationToken = default)
    {
        await SetGripperClosedAsync(true, cancellationToken);
        await SetIpmFixerAsync(true, cancellationToken);
    }

    public bool CanMoveToTeachingPosition(TeachingPosition point, bool live = true)
    {
        return (!IsInsideBuffer(live) || point.Mode == TeachMode.XOnly && AtRotationZ(live))
            && point.Target switch
            {
                TeachingTarget.SupplyBufferHandoff => Rotation == PcbSupplyRotationState.Rotated,
                TeachingTarget.SupplyPcb1Pick or TeachingTarget.SupplyPcb2Pick
                    => Rotation == PcbSupplyRotationState.Unrotated,
                _ => true,
            };
    }

    public async Task MoveToTeachingPositionAsync(
        TeachingPosition point,
        AxisPosition position,
        CancellationToken cancellationToken = default)
    {
        switch (point.Mode)
        {
            case TeachMode.XOnly:
                await MoveXAsync(position.X, cancellationToken);
                break;
            case TeachMode.YOnly:
                await MoveYAsync(position.Y, cancellationToken);
                break;
            case TeachMode.ZOnly:
                await MoveTeachingZAsync(position.Z, cancellationToken);
                break;
            case TeachMode.XZOnly:
            case TeachMode.Full:
                await MoveHorizontalAsync(position.X, position.Y, cancellationToken);
                if (point.Target == TeachingTarget.SupplyBufferHandoff)
                    await MoveToHandoffZAsync(cancellationToken, position.Z);
                else
                    await MoveTeachingZAsync(position.Z, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    public async Task MoveHorizontalAsync(
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        if (InsideBuffer)
        {
            await MoveXAsync(x, cancellationToken);
            if (_motion.GetAxisState(MotionAxis.Y).InPosition
                && Math.Abs(_motion.GetPosition().Y - y) <= MotionService.PositionToleranceMillimeters)
            {
                return;
            }

            await MoveYAsync(y, cancellationToken);
        }
        else
        {
            await MoveYAsync(y, cancellationToken);
            await MoveXAsync(x, cancellationToken);
        }
    }

    internal async Task MoveClearAsync(CancellationToken cancellationToken = default)
    {
        await _motion.MoveAxisAsync(MotionAxis.Z, _settings.BufferClearZ, _settings.Motion.ZSpeed, cancellationToken);
        await _motion.MoveXAtClearZAsync(
            XHome,
            _settings.BufferClearZ,
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

    public async Task<bool> PrepareHomeAsync(CancellationToken cancellationToken = default)
    {
        if (!CanPrepareHome)
        {
            return false;
        }

        await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, true, cancellationToken);

        await _motion.MoveZToPositiveLimitAsync(_settings.Motion.ZHome.SearchSpeed, cancellationToken);
        return true;
    }

    public async Task<bool> CompleteHomeAsync(CancellationToken cancellationToken = default)
    {
        if (!await _motion.HomeFromZPositiveLimitAsync(
            MotionAxis.X,
            _settings.Motion.HorizontalHome.SearchSpeed,
            cancellationToken))
        {
            return false;
        }

        if (!await _motion.HomeFromZPositiveLimitAsync(
            MotionAxis.Y,
            _settings.Motion.HorizontalHome.SearchSpeed,
            cancellationToken))
        {
            return false;
        }

        return await _motion.HomeAsync(
            MotionAxis.Z,
            _settings.Motion.ZHome.SearchSpeed,
            cancellationToken);
    }

    public bool AtReturnEntryZ
    {
        get
        {
            return Rotation == PcbSupplyRotationState.Rotated
                && !_motion.IsMoving
                && _motion.GetAxisState(MotionAxis.Y).InPosition
                && _motion.GetAxisState(MotionAxis.Z).InPosition
                && Math.Abs(_motion.GetPosition().Y - _settings.BufferHandoffPosition.Y) <= MotionService.PositionToleranceMillimeters
                && Math.Abs(_motion.GetPosition().Z - _settings.BufferClearZ) <= MotionService.PositionToleranceMillimeters;
        }
    }

    public bool OnReturnHandoffPath
    {
        get
        {
            return AtHandoffXY
                && Rotation == PcbSupplyRotationState.Rotated
                && _motion.GetPosition().Z >= _settings.BufferHandoffPosition.Z - MotionService.PositionToleranceMillimeters
                && _motion.GetPosition().Z <= _settings.BufferClearZ + MotionService.PositionToleranceMillimeters;
        }
    }

    public Task WaitForPcbAsync(CancellationToken cancellationToken)
    {
        return _io.WaitForInputAsync(InputIo.PcbSupplyPcbDetected, true, cancellationToken);
    }

    public async Task PrepareReturnEntryAsync(CancellationToken cancellationToken)
    {
        await SetGripperClosedAsync(false, cancellationToken);
        await SetIpmFixerAsync(false, cancellationToken);
        await SetRotatedAsync(true, cancellationToken);
        await MoveYAsync(_settings.BufferHandoffPosition.Y, cancellationToken);
        await MoveTeachingZAsync(_settings.BufferClearZ, cancellationToken);
    }

    public Task EnterAtClearZAsync(CancellationToken cancellationToken)
    {
        return Rotation != PcbSupplyRotationState.Rotated
            ? throw new InvalidOperationException("Supply must be rotated before entering the buffer.")
            : _motion.MoveXAtClearZAsync(
                _settings.BufferHandoffPosition.X,
                _settings.BufferClearZ,
                _settings.Motion.HorizontalSpeed,
                cancellationToken);
    }

    public async Task ReturnWithPcbAsync(CancellationToken cancellationToken)
    {
        await MoveToRotationZAsync(cancellationToken);
        await MoveXAsync(XHome, cancellationToken);
        await MoveYAsync(_settings.CarrierY, cancellationToken);
        await SetRotatedAsync(false, cancellationToken);
    }

    public Task MoveXAsync(double x, CancellationToken cancellationToken = default)
    {
        return _motion.MoveAxisAsync(MotionAxis.X, x, _settings.Motion.HorizontalSpeed, cancellationToken);
    }

    public Task MoveYAsync(double y, CancellationToken cancellationToken = default)
    {
        return InsideBuffer
            ? throw new InvalidOperationException("Supply Y cannot move inside the buffer.")
            : _motion.MoveAxisAsync(MotionAxis.Y, y, _settings.Motion.HorizontalSpeed, cancellationToken);
    }

    public Task MoveTeachingZAsync(double z, CancellationToken cancellationToken = default)
    {
        return InsideBuffer
            ? throw new InvalidOperationException("Supply Z cannot move inside the buffer.")
            : _motion.MoveAxisAsync(MotionAxis.Z, z, _settings.Motion.ZSpeed, cancellationToken);
    }

    public bool CanJog(MotionAxis axis, bool live = true)
    {
        return axis switch
        {
            MotionAxis.X => AtRotationZ(live),
            MotionAxis.Y => _motion.HasY && !IsInsideBuffer(live) && AtRotationZ(live),
            MotionAxis.Z => _motion.HasZ && !IsInsideBuffer(live),
            _ => false,
        };
    }

    public Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        if (axis is MotionAxis.Y or MotionAxis.Z && InsideBuffer)
            throw new InvalidOperationException($"Supply {axis} cannot jog inside the buffer.");
        return _motion.JogAsync(axis, velocity, cancellationToken);
    }

    private bool InsideBuffer
    {
        get
        {
            return IsInsideBuffer(live: true);
        }
    }

    public bool IsInsideBuffer(bool live)
    {
        return _bufferSettings.ContainsSupplyX(live ? _motion.GetPosition().X : Motion.Position.X);
    }

    private bool AtRotationZ(bool live)
    {
        return live ? IsAtRotationZ : Motion.IsAtZ(_settings.RotationZ);
    }

    public Task MoveToRotationZAsync(CancellationToken cancellationToken = default)
    {
        return _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    public async Task SetRotatedAsync(bool rotated, CancellationToken cancellationToken = default)
    {
        if (InsideBuffer)
        {
            throw new InvalidOperationException("Supply cannot rotate inside the buffer.");
        }

        await _motion.MoveToHorizontalZAsync(cancellationToken);
        await _io.SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, rotated, cancellationToken);
    }

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.PcbSupplyAvailableFromFront1
            or InputIo.PcbSupplyUnrotated
            or InputIo.PcbSupplyRotated
            or InputIo.PcbSupplyGripperClosed
            or InputIo.PcbSupplyGripperOpen
            or InputIo.PcbSupplyIpmFixerForward
            or InputIo.PcbSupplyIpmFixerBackward
            or InputIo.PcbSupplyPcbDetected)
        {
            Changed?.Invoke();
        }
    }

    private PcbSupplyCylinderState CylinderState(InputIo forwardInput, InputIo backwardInput)
    {
        return (_io.GetInput(backwardInput), _io.GetInput(forwardInput)) switch
        {
            (true, false) => PcbSupplyCylinderState.Backward,
            (false, true) => PcbSupplyCylinderState.Forward,
            _ => PcbSupplyCylinderState.Between,
        };
    }
}
