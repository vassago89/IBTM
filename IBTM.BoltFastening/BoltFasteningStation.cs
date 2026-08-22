using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningStation(
    IBoltHead shootingHead,
    IBoltHead pickupHead,
    IIoService io,
    IXyMotion motion,
    BoltFasteningSettings settings)
{
    public bool FeederRunCommandOn =>
        io.GetOutput(OutputIo.ShootingFeederRun);

    public bool HousingPresent(HousingSlot housing) =>
        io.GetInput(housing == HousingSlot.Housing1
            ? InputIo.BoltFasteningHousing1Present
            : InputIo.BoltFasteningHousing2Present);

    public Task WaitForBackupPlateAsync(
        bool up,
        CancellationToken cancellationToken = default) =>
        io.WaitForInputAsync(
            InputIo.BoltFasteningBackupPlateUp,
            up,
            cancellationToken);

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await shootingHead.InitializeAsync(cancellationToken);
        await pickupHead.InitializeAsync(cancellationToken);
    }

    public async Task<BoltResult> FastenAsync(
        BoltPoint bolt,
        CancellationToken cancellationToken = default)
    {
        var position = settings.GetBoltPosition(bolt);
        var headDown = bolt.Head == FasteningHead.Pickup
            ? OutputIo.BoltHead1Down
            : OutputIo.BoltHead2Down;

        await RetractHeadsAsync(cancellationToken);
        await motion.MoveToAsync(
            position.X,
            position.Y,
            position.Z,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            headDown,
            true,
            cancellationToken);
        var result = await GetHead(bolt.Head).TightenAsync(
            bolt.Preset,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            headDown,
            false,
            cancellationToken);
        return result;
    }

    public async Task MoveToSafeZAsync(
        CancellationToken cancellationToken = default)
    {
        await RetractHeadsAsync(cancellationToken);
        await motion.MoveToSafeZAsync(cancellationToken);
    }

    public void Stop() =>
        io.SetOutput(OutputIo.ShootingFeederRun, false);

    private IBoltHead GetHead(FasteningHead head) => head switch
    {
        FasteningHead.Shooting => shootingHead,
        FasteningHead.Pickup => pickupHead,
        _ => throw new System.ArgumentOutOfRangeException(nameof(head)),
    };

    private Task RetractHeadsAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(
            io.SetOutputAndWaitAsync(
                OutputIo.BoltHead1Down,
                false,
                cancellationToken),
            io.SetOutputAndWaitAsync(
                OutputIo.BoltHead2Down,
                false,
                cancellationToken));
}
