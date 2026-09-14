using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyHandler : IPcbHandoffState
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

    public bool IsAtRotationZ(bool live = true)
    {
        return live ? _motion.IsAtHorizontalZ : Motion.IsAtZ(_settings.RotationZ);
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

    public bool PcbSecured
    {
        get
        {
            return Pcb == PcbSupplyPcbState.Secured;
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

    public void SetUpstreamReady(bool ready)
    {
        _io.SetOutput(OutputIo.PcbSupplyReadyToFront1, ready);
    }

    public async Task MoveToHandoffAsync(CancellationToken cancellationToken, XyPosition? position = null)
    {
        if (Rotation != PcbSupplyRotationState.Rotated)
            throw new InvalidOperationException("Supply must be rotated before moving to the handoff position.");
        position ??= _settings.BufferHandoffPosition;
        await _motion.MoveToHorizontalZAsync(cancellationToken);
        await MoveHorizontalAsync(position.X, position.Y, cancellationToken);
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

    public bool CanMoveToTeachingPosition(TeachingPosition point, bool live = true)
    {
        return (IsInsideBuffer(live) == false
            || point.Mode == TeachMode.XOnly && IsAtRotationZ(live))
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
                await MoveAxisAsync(MotionAxis.X, position.X, cancellationToken);
                break;
            case TeachMode.YOnly:
                await MoveAxisAsync(MotionAxis.Y, position.Y, cancellationToken);
                break;
            case TeachMode.ZOnly:
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
                break;
            case TeachMode.XYOnly:
                await MoveToHandoffAsync(cancellationToken, new() { X = position.X, Y = position.Y });
                break;
            case TeachMode.XZOnly:
                await MoveHorizontalAsync(position.X, position.Y, cancellationToken);
                await MoveAxisAsync(MotionAxis.Z, position.Z, cancellationToken);
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
        if (IsInsideBuffer(live: true) == true)
        {
            await MoveAxisAsync(MotionAxis.X, x, cancellationToken);
            if (_motion.GetAxisState(MotionAxis.Y).InPosition
                && Math.Abs(_motion.GetPosition().Y - y) <= MotionService.PositionToleranceMillimeters)
            {
                return;
            }

            await MoveAxisAsync(MotionAxis.Y, y, cancellationToken);
        }
        else
        {
            await MoveAxisAsync(MotionAxis.Y, y, cancellationToken);
            await MoveAxisAsync(MotionAxis.X, x, cancellationToken);
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

    public Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        CancellationToken cancellationToken = default)
    {
        if (axis is MotionAxis.Y or MotionAxis.Z
            && IsInsideBuffer(live: true) == true)
            throw new InvalidOperationException($"Supply {axis} cannot move inside the buffer.");
        var speed = axis == MotionAxis.Z ? _settings.Motion.ZSpeed : _settings.Motion.HorizontalSpeed;
        return _motion.MoveAxisAsync(axis, position, speed, cancellationToken);
    }

    public bool CanJog(MotionAxis axis, bool live = true)
    {
        return axis switch
        {
            MotionAxis.X => IsAtRotationZ(live),
            MotionAxis.Y => _motion.HasY && IsInsideBuffer(live) == false && IsAtRotationZ(live),
            MotionAxis.Z => _motion.HasZ && IsInsideBuffer(live) == false,
            _ => false,
        };
    }

    public Task JogAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default)
    {
        if (axis is MotionAxis.Y or MotionAxis.Z
            && IsInsideBuffer(live: true) == true)
            throw new InvalidOperationException($"Supply {axis} cannot jog inside the buffer.");
        return _motion.JogAsync(axis, velocity, cancellationToken);
    }

    public bool? IsInsideBuffer(bool live)
    {
        var x = live ? _motion.GetPosition().X : Motion.Position.X;
        return x is { } position ? _bufferSettings.ContainsSupplyX(position) : null;
    }

    public Task MoveToRotationZAsync(CancellationToken cancellationToken = default)
    {
        return _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    public async Task SetRotatedAsync(bool rotated, CancellationToken cancellationToken = default)
    {
        if (IsInsideBuffer(live: true) == true)
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
