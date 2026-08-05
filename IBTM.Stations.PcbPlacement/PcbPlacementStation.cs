using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Stations.PcbPlacement;

public sealed class PcbPlacementStation(
    MotionService motion,
    ICamera alignmentCamera,
    IIoService io,
    PcbPlacementSettings settings)
{
    public bool PcbPresent =>
        io.GetInput(InputIo.PcbPlacementPcbPresent);
    public bool GripperClosed =>
        io.GetInput(InputIo.PcbPlacementGripperClosed);
    public bool HousingPresent =>
        io.GetInput(InputIo.PcbPlacementHousing1Present)
        || io.GetInput(InputIo.PcbPlacementHousing2Present);

    public void Initialize()
    {
        motion.Initialize();
        alignmentCamera.Initialize();
        SetLaser(false);
    }

    public void Stop()
    {
        motion.Stop();
        SetLaser(false);
    }

    public void EmergencyStop()
    {
        motion.EmergencyStop();
        SetLaser(false);
    }

    public async Task PickFromBufferAsync(
        AxisPos clearPosition,
        CancellationToken cancellationToken)
    {
        await motion.MoveToAsync(
            settings.BufferPosition.X,
            settings.BufferPosition.Y,
            settings.BufferPosition.Z,
            cancellationToken);
        await SetGripperAsync(true, cancellationToken);
        await motion.MoveToAsync(
            clearPosition.X,
            clearPosition.Y,
            clearPosition.Z,
            cancellationToken);
    }

    public Task MoveClearAsync(
        AxisPos clearPosition,
        CancellationToken cancellationToken = default)
        => motion.MoveToAsync(
            clearPosition.X,
            clearPosition.Y,
            clearPosition.Z,
            cancellationToken);

    public Task SetGripperAsync(
        bool closed,
        CancellationToken cancellationToken = default) =>
        io.SetOutputAndWaitAsync(
            OutputIo.PcbPlacementGripperClose,
            closed,
            cancellationToken);

    public void SetLaser(bool on) =>
        io.SetOutput(OutputIo.PcbPlacementLaser, on);
}
