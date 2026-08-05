using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyHandler(
    MotionService motion,
    IIoService io,
    PcbSupplySettings settings) : IDisposable
{
    private const double OutsideX = 0.0;

    public PcbSupplyRotation Rotation { get; private set; }
    public bool PcbPresent =>
        io.GetInput(InputIo.PcbSupplyPcbPresent);
    public bool GripperClosed =>
        io.GetInput(InputIo.PcbSupplyGripperClosed);
    public bool CarrierAvailable =>
        io.GetInput(InputIo.PcbSupplyUpstreamBoardAvailable);

    public bool CanHome(bool bufferPcbPresent) =>
        Rotation != PcbSupplyRotation.Between
        && (Rotation != PcbSupplyRotation.Unrotated
            || !PcbPresent && !bufferPcbPresent);

    public void Initialize()
    {
        motion.Initialize();
        io.SetOutput(OutputIo.PcbSupplyUpstreamMachineReady, false);
        io.InputChanged += OnInputChanged;
        RefreshRotation();
    }

    public void Stop()
    {
        motion.Stop();
        io.SetOutput(OutputIo.PcbSupplyUpstreamMachineReady, false);
    }

    public void EmergencyStop()
    {
        motion.EmergencyStop();
        io.SetOutput(OutputIo.PcbSupplyUpstreamMachineReady, false);
    }

    public async Task PlaceOnBufferAsync(
        CancellationToken cancellationToken)
    {
        await motion.MoveToXZAsync(
            settings.BufferPosition.X,
            settings.BufferPosition.Z,
            cancellationToken);
        await SetGripperAsync(false, cancellationToken);
        await MoveClearAsync(cancellationToken);
    }

    public async Task MoveClearAsync(
        CancellationToken cancellationToken = default)
    {
        await motion.MoveToXAsync(
            OutsideX,
            settings.Motion.HorizontalSpeed,
            cancellationToken);
        await SetRotatedAsync(false, cancellationToken);
    }

    public Task SetGripperAsync(
        bool closed,
        CancellationToken cancellationToken = default) =>
        io.SetOutputAndWaitAsync(
            OutputIo.PcbSupplyGripperClose,
            closed,
            cancellationToken);

    public Task MoveZToPositiveLimitAsync(
        double velocity,
        CancellationToken cancellationToken = default)
    {
        if (Rotation != PcbSupplyRotation.Rotated)
        {
            throw new InvalidOperationException(
                "Supply must be rotated before moving Z to its positive limit.");
        }

        return motion.MoveZToPositiveLimitAsync(
            velocity,
            cancellationToken);
    }

    public async Task<bool> HomeAsync(
        bool bufferPcbPresent,
        double horizontalVelocity,
        double zVelocity,
        CancellationToken cancellationToken = default)
    {
        if (!CanHome(bufferPcbPresent))
        {
            return false;
        }

        if (Rotation == PcbSupplyRotation.Unrotated)
        {
            await io.SetOutputAndWaitAsync(
                OutputIo.PcbSupplyRotate,
                true,
                cancellationToken);
        }

        await MoveZToPositiveLimitAsync(zVelocity, cancellationToken);
        if (!await motion.HomeXFromZPositiveLimitAsync(
                horizontalVelocity,
                cancellationToken))
        {
            return false;
        }

        return await motion.HomeAsync(
            MotionAxis.Z,
            zVelocity,
            cancellationToken);
    }

    public async Task SetRotatedAsync(
        bool rotated,
        CancellationToken cancellationToken = default)
    {
        await motion.MoveToSafeZAsync(cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.PcbSupplyRotate,
            rotated,
            cancellationToken);
    }

    public void Dispose() => io.InputChanged -= OnInputChanged;

    private void RefreshRotation()
    {
        var unrotated = io.GetInput(InputIo.PcbSupplyUnrotated);
        var rotated = io.GetInput(InputIo.PcbSupplyRotated);
        var rotation = (unrotated, rotated) switch
        {
            (true, false) => PcbSupplyRotation.Unrotated,
            (false, true) => PcbSupplyRotation.Rotated,
            _ => PcbSupplyRotation.Between,
        };

        if (Rotation == rotation)
        {
            return;
        }

        Rotation = rotation;
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.PcbSupplyUnrotated
            or InputIo.PcbSupplyRotated)
        {
            RefreshRotation();
        }
    }

}
