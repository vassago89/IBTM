using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Stations.BoltFastening;

public sealed class BoltFasteningStation(
    MotionService motion,
    IBoltHead standardHead,
    IBoltHead loctiteHead,
    IIoService io,
    BoltFasteningSettings settings)
{
    public bool CarrierJigPresent =>
        io.GetInput(InputIo.BoltFasteningCarrierJigPresent);
    public bool LoctiteVacuumDetected =>
        io.GetInput(InputIo.BoltFasteningLoctiteVacuumDetected);

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        motion.Initialize();
        await Task.WhenAll(
            standardHead.InitializeAsync(cancellationToken),
            loctiteHead.InitializeAsync(cancellationToken));
    }

    public void Stop()
    {
        motion.Stop();
        standardHead.Stop();
        loctiteHead.Stop();
    }

    public void EmergencyStop()
    {
        motion.EmergencyStop();
        standardHead.EmergencyStop();
        loctiteHead.EmergencyStop();
    }

    public Task MoveToLoctitePickupAsync(
        CancellationToken cancellationToken = default) =>
        motion.MoveToAsync(
            settings.LoctitePickupPosition.X,
            settings.LoctitePickupPosition.Y,
            settings.LoctitePickupPosition.Z,
            cancellationToken);

    public void SetLoctiteVacuum(bool on) =>
        io.SetOutput(OutputIo.BoltFasteningLoctiteVacuumPump, on);
}
