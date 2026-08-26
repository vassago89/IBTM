using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyHandler
{
    private const double XHome = 0;

    private readonly IAxisMotion _motion;
    private readonly IIoService _io;
    private readonly PcbSupplySettings _settings;

    public PcbSupplyHandler(
        IAxisMotion motion,
        IIoService io,
        PcbSupplySettings settings)
    {
        _motion = motion;
        _io = io;
        _settings = settings;
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

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

    public PcbSupplyRotation Rotation =>
        (_io.GetInput(InputIo.PcbSupplyUnrotated),
            _io.GetInput(InputIo.PcbSupplyRotated)) switch
        {
            (true, false) => PcbSupplyRotation.Unrotated,
            (false, true) => PcbSupplyRotation.Rotated,
            _ => PcbSupplyRotation.Between,
        };

    public bool CanPrepareHome =>
        Rotation != PcbSupplyRotation.Between
        && (Rotation != PcbSupplyRotation.Unrotated
            || Pcb == PcbSupplyPcbState.None);

    public void SetUpstreamReady(bool ready) =>
        _io.SetOutput(OutputIo.PcbSupplyReadyToFront1, ready);

    public async Task MoveToHandoffAsync(
        CancellationToken cancellationToken)
    {
        EnsureRotated();
        await MoveHorizontalAsync(
            _settings.BufferHandoffPosition.X,
            _settings.BufferHandoffPosition.Y,
            cancellationToken);
        await _motion.MoveZAsync(
            _settings.BufferHandoffPosition.Z,
            _settings.Motion.ZSpeed,
            cancellationToken);
    }

    public async Task<bool> PickAsync(
        PcbPickPosition position,
        CancellationToken cancellationToken = default)
    {
        EnsureUnrotated();
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
            await SetNestAsync(true, cancellationToken);
            await SetIpmFixerAsync(true, cancellationToken);
        }

        await _motion.MoveToHorizontalZAsync(cancellationToken);
        return Pcb != PcbSupplyPcbState.None;
    }

    public async Task MoveHorizontalAsync(
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        await _motion.MoveYAsync(
            y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
        await _motion.MoveXAsync(
            x,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    public async Task MoveClearAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureRotated();
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
        EnsureRotated();
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

    private void EnsureRotated()
    {
        if (Rotation != PcbSupplyRotation.Rotated)
        {
            throw new InvalidOperationException(
                "Supply must be rotated for Buffer movement.");
        }
    }

    private void EnsureUnrotated()
    {
        if (Rotation != PcbSupplyRotation.Unrotated)
        {
            throw new InvalidOperationException(
                "Supply must be unrotated for PCB pickup.");
        }
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
