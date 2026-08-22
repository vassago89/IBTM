using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyHandler
{
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

    public bool IsMoving => _motion.IsMoving;
    public bool CarrierAvailable =>
        _io.GetInput(InputIo.PcbSupplyAvailableFromFront1);
    public bool PcbDetected =>
        _io.GetInput(InputIo.PcbSupplyPcbDetected);
    public bool NestForward =>
        _io.GetInput(InputIo.PcbSupplyNestForward);
    public bool IpmFixerForward =>
        _io.GetInput(InputIo.PcbSupplyIpmFixerForward);
    public bool PcbSecured =>
        PcbDetected && NestForward && IpmFixerForward;

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
        && (Rotation != PcbSupplyRotation.Unrotated || !PcbDetected);

    public void SetReady(bool ready)
    {
        if (_io.GetOutput(OutputIo.PcbSupplyReadyToFront1) != ready)
        {
            _io.SetOutput(OutputIo.PcbSupplyReadyToFront1, ready);
        }
    }

    public async Task MoveToHandoffAsync(
        CancellationToken cancellationToken)
    {
        EnsureRotated();
        await MoveToAsync(
            _settings.BufferHandoffPosition.X,
            _settings.BufferHandoffPosition.Y,
            _settings.BufferHandoffPosition.Z,
            cancellationToken);
    }

    public async Task<bool> PickAsync(
        PcbPickPosition position,
        CancellationToken cancellationToken = default)
    {
        EnsureUnrotated();
        await MoveToAsync(
            position.X,
            _settings.CarrierY,
            position.Z,
            cancellationToken);
        if (PcbDetected)
        {
            await SetNestAsync(true, cancellationToken);
            await SetIpmFixerAsync(true, cancellationToken);
        }

        await _motion.MoveToSafeZAsync(cancellationToken);
        return PcbDetected;
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
            _settings.OutsideX,
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

        if (Rotation == PcbSupplyRotation.Unrotated)
        {
            await _io.SetOutputAndWaitAsync(
                OutputIo.PcbSupplyRotate,
                true,
                cancellationToken);
        }

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
        await _motion.MoveToSafeZAsync(cancellationToken);
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

    private async Task MoveToAsync(
        double x,
        double y,
        double z,
        CancellationToken cancellationToken)
    {
        await _motion.MoveToSafeZAsync(cancellationToken);
        await _motion.MoveYAsync(
            y,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
        await _motion.MoveXAsync(
            x,
            _settings.Motion.HorizontalSpeed,
            cancellationToken);
        await _motion.MoveZAsync(
            z,
            _settings.Motion.ZSpeed,
            cancellationToken);
    }

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.PcbSupplyAvailableFromFront1
            or InputIo.PcbSupplyUnrotated
            or InputIo.PcbSupplyRotated
            or InputIo.PcbSupplyNestForward
            or InputIo.PcbSupplyIpmFixerForward
            or InputIo.PcbSupplyPcbDetected)
        {
            Changed?.Invoke();
        }
    }
}
