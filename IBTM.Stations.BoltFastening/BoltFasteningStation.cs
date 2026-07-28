using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Transport;

namespace IBTM.Stations.BoltFastening;

public sealed class BoltFasteningStation(
    MotionService motion,
    IIoService io,
    BoltFasteningSettings settings,
    ProcessEvents events,
    IBoltHead standardHead,
    IBoltHead loctiteHead)
{
    public CarrierJigPositioner CarrierJigPositioner { get; } = new(
        io,
        InputIo.BoltFasteningCarrierJigPresent,
        OutputIo.BoltFasteningStopperUp,
        OutputIo.BoltFasteningBackupPlateUp);

    public void Initialize()
    {
        motion.Initialize();
        CarrierJigPositioner.Initialize();
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await standardHead.InitializeAsync(cancellationToken);
        await loctiteHead.InitializeAsync(cancellationToken);
    }

    public async Task ProcessAsync(
        BoltFasteningRecipe recipe,
        CarrierJigState carrierJig,
        CancellationToken cancellationToken)
    {
        await events.RunStageAsync(
            ProcessStage.PositionBoltFasteningCarrierJig,
            cancellationToken,
            CarrierJigPositioner.PositionAsync);

        if (!carrierJig.Pcb1Present && !carrierJig.Pcb2Present)
        {
            return;
        }

        await events.RunStageAsync(
            ProcessStage.TightenBolts,
            cancellationToken,
            token => TightenBoltsAsync(recipe, carrierJig, token));
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

    private async Task TightenBoltsAsync(
        BoltFasteningRecipe recipe,
        CarrierJigState carrierJig,
        CancellationToken cancellationToken)
    {
        var pcbCentres = new[]
        {
            (
                X: recipe.Pcb1Reference.X,
                Y: recipe.Pcb1Reference.Y,
                Slot: PcbSlot.Pcb1,
                Present: carrierJig.Pcb1Present),
            (
                X: recipe.Pcb2Reference.X,
                Y: recipe.Pcb2Reference.Y,
                Slot: PcbSlot.Pcb2,
                Present: carrierJig.Pcb2Present),
        };
        var pcbCount = (carrierJig.Pcb1Present ? 1 : 0)
            + (carrierJig.Pcb2Present ? 1 : 0);
        var totalBolts = recipe.BoltPoints.Count * pcbCount;
        var boltIndex = 0;

        foreach (var pcb in pcbCentres)
        {
            if (!pcb.Present)
            {
                continue;
            }

            foreach (var boltPoint in recipe.BoltPoints)
            {
                boltIndex++;
                var head = GetHead(boltPoint.BoltType);
                var headSettings = settings.GetHead(boltPoint.BoltType);
                events.BoltProgressed(
                    boltIndex,
                    totalBolts,
                    pcb.Slot,
                    boltPoint.Number);
                await motion.MoveToAsync(
                    pcb.X + boltPoint.X - headSettings.OffsetX,
                    pcb.Y + boltPoint.Y - headSettings.OffsetY,
                    boltPoint.Z,
                    cancellationToken);

                await head.SupplyAsync(cancellationToken);
                events.Bolt(await TightenAsync(
                    head,
                    boltPoint.TargetTorqueNm,
                    cancellationToken));
                await motion.MoveToSafeZAsync(cancellationToken);
            }
        }
    }

    private async Task<BoltResult> TightenAsync(
        IBoltHead head,
        double targetTorque,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await head.TightenAsync(targetTorque, cancellationToken);
            if (result.Success || attempt >= settings.RetryCount)
            {
                return result;
            }
        }
    }
    private IBoltHead GetHead(BoltType boltType) => boltType switch
    {
        BoltType.Standard => standardHead,
        BoltType.Loctite => loctiteHead,
        _ => throw new System.ArgumentOutOfRangeException(nameof(boltType)),
    };
}
