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
    public IMotionFeedback Feedback => _motion;
    public bool IsAtRotationZ => _motion.IsAtHorizontalZ;

    public bool UpstreamCarrierAvailable =>
        _io.GetInput(InputIo.PcbSupplyAvailableFromFront1);
    public PcbSupplyCylinderState Nest => CylinderState(
        InputIo.PcbSupplyNestForward,
        InputIo.PcbSupplyNestBackward);
    public PcbSupplyCylinderState IpmFixer => CylinderState(
        InputIo.PcbSupplyIpmFixerForward,
        InputIo.PcbSupplyIpmFixerBackward);
    public PcbSupplyPcbState Pcb
    {
        get
        {
            if (!_io.GetInput(InputIo.PcbSupplyPcbDetected))
            {
                return PcbSupplyPcbState.None;
            }

            return Nest == PcbSupplyCylinderState.Forward
                && IpmFixer == PcbSupplyCylinderState.Forward
                    ? PcbSupplyPcbState.Secured
                    : PcbSupplyPcbState.Detected;
        }
    }

    public PcbSupplyRotationState Rotation =>
        (_io.GetInput(InputIo.PcbSupplyUnrotated),
            _io.GetInput(InputIo.PcbSupplyRotated)) switch
        {
            (true, false) => PcbSupplyRotationState.Unrotated,
            (false, true) => PcbSupplyRotationState.Rotated,
            _ => PcbSupplyRotationState.Between,
        };

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

    public void InitializeMotion() => _motion.Initialize();

    public void ResetMotion() => _motion.Reset();

    public void SetServo(MotionAxis axis, bool on) =>
        _motion.SetServo(axis, on);

    public bool AtHandoffXY
    {
        get
        {
            var position = _motion.GetPosition();
            return !_motion.IsMoving
                && _motion.GetAxisState(MotionAxis.X).InPosition
                && _motion.GetAxisState(MotionAxis.Y).InPosition
                && Math.Abs(position.X - _settings.BufferHandoffPosition.X)
                    <= MotionService.PositionToleranceMillimeters
                && Math.Abs(position.Y - _settings.BufferHandoffPosition.Y)
                    <= MotionService.PositionToleranceMillimeters;
        }
    }

    public void SetUpstreamReady(bool ready) =>
        _io.SetOutput(OutputIo.PcbSupplyReadyToFront1, ready);

    public Task WaitForUpstreamCarrierAsync(
        bool available,
        CancellationToken cancellationToken = default) =>
        _io.WaitForInputAsync(
            InputIo.PcbSupplyAvailableFromFront1,
            available,
            cancellationToken);

    public async Task MoveAboveHandoffAsync(
        CancellationToken cancellationToken)
    {
        await _motion.MoveToHorizontalZAsync(cancellationToken);
        await MoveHorizontalAsync(
            _settings.BufferHandoffPosition.X,
            _settings.BufferHandoffPosition.Y,
            cancellationToken);
    }

    public Task LowerToHandoffAsync(
        CancellationToken cancellationToken) =>
        Rotation != PcbSupplyRotationState.Rotated
            ? throw new InvalidOperationException(
                "Supply must be rotated before lowering into the buffer.")
            : _motion.MoveZAsync(
                _settings.BufferHandoffPosition.Z,
                _settings.Motion.ZSpeed,
                cancellationToken);

    public async Task PickAsync(
        PcbPickPosition position,
        CancellationToken cancellationToken = default)
    {
        await MoveHorizontalAsync(
            position.X,
            _settings.CarrierY,
            cancellationToken);
        await _motion.MoveZAsync(
            position.Z,
            _settings.Motion.ZSpeed,
            cancellationToken);
        if (Pcb != PcbSupplyPcbState.None)
        {
            await SecurePcbAsync(cancellationToken);
            return;
        }

        await _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    public async Task SecurePcbAsync(
        CancellationToken cancellationToken = default)
    {
        await SetNestAsync(true, cancellationToken);
        await SetIpmFixerAsync(true, cancellationToken);
        await _motion.MoveToHorizontalZAsync(cancellationToken);
    }

    public async Task MoveHorizontalAsync(
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        if (InsideBuffer)
        {
            await MoveXAsync(x, cancellationToken);
            await MoveYAsync(y, cancellationToken);
        }
        else
        {
            await MoveYAsync(y, cancellationToken);
            await MoveXAsync(x, cancellationToken);
        }
    }

    public async Task MoveClearAsync(
        CancellationToken cancellationToken = default)
    {
        await _motion.MoveZAsync(
            _settings.BufferClearZ,
            _settings.Motion.ZSpeed,
            cancellationToken);
        await _motion.MoveXAtClearZAsync(
            XHome,
            _settings.BufferClearZ,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    public Task SetIpmFixerAsync(
        bool forward,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.PcbSupplyIpmFixerForward,
            forward,
            cancellationToken);

    public Task SetNestAsync(
        bool forward,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.PcbSupplyNestForward,
            forward,
            cancellationToken);

    public async Task<bool> PrepareHomeAsync(
        double zVelocity,
        CancellationToken cancellationToken = default)
    {
        if (!CanPrepareHome)
        {
            return false;
        }

        await _io.SetOutputAndWaitAsync(
            OutputIo.PcbSupplyRotate,
            true,
            cancellationToken);

        await _motion.MoveZToPositiveLimitAsync(
            zVelocity,
            cancellationToken);
        return true;
    }

    public async Task<bool> CompleteHomeAsync(
        double horizontalVelocity,
        double zVelocity,
        CancellationToken cancellationToken = default)
    {
        if (!await _motion.HomeFromZPositiveLimitAsync(
                MotionAxis.X,
                horizontalVelocity,
                cancellationToken))
        {
            return false;
        }

        if (!await _motion.HomeFromZPositiveLimitAsync(
                MotionAxis.Y,
                horizontalVelocity,
                cancellationToken))
        {
            return false;
        }

        return await _motion.HomeAsync(
            MotionAxis.Z,
            zVelocity,
            cancellationToken);
    }

    public Task MoveXAsync(
        double x,
        CancellationToken cancellationToken = default) =>
        _motion.MoveXAsync(
            x,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);

    public Task MoveYAsync(
        double y,
        CancellationToken cancellationToken = default) =>
        InsideBuffer
            ? throw new InvalidOperationException(
                "Supply Y cannot move inside the buffer.")
            : _motion.MoveYAsync(
            y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);

    public Task MoveTeachingZAsync(
        double z,
        CancellationToken cancellationToken = default) =>
        InsideBuffer
            ? throw new InvalidOperationException(
                "Supply Z cannot move inside the buffer.")
            : _motion.MoveZAsync(
                z,
                _settings.Motion.ZSpeed,
                cancellationToken);

    public void Jog(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        switch (axis)
        {
            case MotionAxis.X:
                _motion.JogX(velocity, cancellationToken);
                break;
            case MotionAxis.Y:
                if (InsideBuffer)
                {
                    throw new InvalidOperationException(
                        "Supply Y cannot jog inside the buffer.");
                }
                _motion.JogY(velocity, cancellationToken);
                break;
            case MotionAxis.Z:
                if (InsideBuffer)
                {
                    throw new InvalidOperationException(
                        "Supply Z cannot jog inside the buffer.");
                }
                _motion.JogZ(velocity, cancellationToken);
                break;
        }
    }

    private bool InsideBuffer =>
        _bufferSettings.ContainsSupplyX(_motion.GetPosition().X);

    public Task MoveToRotationZAsync(
        CancellationToken cancellationToken = default) =>
        _motion.MoveToHorizontalZAsync(cancellationToken);

    public async Task SetRotatedAsync(
        bool rotated,
        CancellationToken cancellationToken = default)
    {
        await _motion.MoveToHorizontalZAsync(cancellationToken);
        await _io.SetOutputAndWaitAsync(
            OutputIo.PcbSupplyRotate,
            rotated,
            cancellationToken);
    }

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.PcbSupplyAvailableFromFront1
            or InputIo.PcbSupplyUnrotated
            or InputIo.PcbSupplyRotated
            or InputIo.PcbSupplyNestForward
            or InputIo.PcbSupplyNestBackward
            or InputIo.PcbSupplyIpmFixerForward
            or InputIo.PcbSupplyIpmFixerBackward
            or InputIo.PcbSupplyPcbDetected)
        {
            Changed?.Invoke();
        }
    }

    private PcbSupplyCylinderState CylinderState(
        InputIo forwardInput,
        InputIo backwardInput) =>
        (_io.GetInput(backwardInput), _io.GetInput(forwardInput)) switch
        {
            (true, false) => PcbSupplyCylinderState.Backward,
            (false, true) => PcbSupplyCylinderState.Forward,
            _ => PcbSupplyCylinderState.Between,
        };
}
