using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Transport;

namespace IBTM.Stations.Inspection;

public sealed class InspectionStation(
    MotionService motion,
    IIoService io,
    InspectionSettings settings,
    ProcessEvents events,
    ICameraStreamService camera,
    ILightController light,
    LightingSettings lighting)
{
    private const int MinimumFeaturePixels = 400;

    public int NgStackCount { get; private set; }
    public int NgStackCapacity => settings.NgStackMaxCount;
    public CarrierJigPositioner CarrierJigPositioner { get; } = new(
        io,
        InputIo.InspectionCarrierJigPresent,
        OutputIo.InspectionStopperUp,
        OutputIo.InspectionBackupPlateUp);

    public void Initialize()
    {
        motion.Initialize();
        camera.Initialize();
        CarrierJigPositioner.Initialize();
    }

    public async Task<CarrierInspectionResult> ProcessAsync(
        InspectionRecipe recipe,
        CarrierJigState carrierJig,
        CancellationToken cancellationToken)
    {
        await events.RunStageAsync(
            ProcessStage.PositionInspectionCarrierJig,
            cancellationToken,
            CarrierJigPositioner.PositionAsync);

        var result = await events.RunStageAsync(
            ProcessStage.Inspect,
            cancellationToken,
            token => InspectAsync(recipe, carrierJig, token));

        if (result.OverallResult == InspectionResult.Ng)
        {
            await StackNgCarrierJigAsync(recipe, cancellationToken);
        }

        return result;
    }

    public void ResetNgStack()
    {
        NgStackCount = 0;
        events.NgStack(0, alarm: false);
    }

    public void Stop() => motion.Stop();

    public void EmergencyStop() => motion.EmergencyStop();

    private async Task<CarrierInspectionResult> InspectAsync(
        InspectionRecipe recipe,
        CarrierJigState carrierJig,
        CancellationToken cancellationToken)
    {
        var pcb1 = carrierJig.Pcb1Present
            ? await InspectPcbAsync(
                recipe.Pcb1InspectionPosition,
                cancellationToken)
            : new PcbInspectionResult(InspectionResult.Missing, null);
        var pcb2 = carrierJig.Pcb2Present
            ? await InspectPcbAsync(
                recipe.Pcb2InspectionPosition,
                cancellationToken)
            : new PcbInspectionResult(InspectionResult.Missing, null);
        var result = new CarrierInspectionResult(pcb1, pcb2);
        events.Inspection(result);
        return result;
    }

    private async Task<PcbInspectionResult> InspectPcbAsync(
        AxisPos position,
        CancellationToken cancellationToken)
    {
        await motion.MoveToAsync(
            position.X,
            position.Y,
            position.Z,
            cancellationToken);

        light.SetLevel(lighting.InspectionChannel, lighting.InspectionLevel);
        light.TurnOn(lighting.InspectionChannel);
        ImageFrame image;
        try
        {
            image = camera.Capture();
        }
        finally
        {
            light.TurnOff(lighting.InspectionChannel);
            await motion.MoveToSafeZAsync(cancellationToken);
        }

        var featurePixels = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var index = (y * image.Stride) + (x * 3);
                if (image.Pixels[index] >= 100
                    && image.Pixels[index + 1] >= 100
                    && image.Pixels[index + 2] >= 100)
                {
                    featurePixels++;
                }
            }
        }

        var result = featurePixels >= MinimumFeaturePixels
            ? InspectionResult.Good
            : InspectionResult.Ng;
        return new PcbInspectionResult(result, image);
    }

    private Task StackNgCarrierJigAsync(
        InspectionRecipe recipe,
        CancellationToken cancellationToken) =>
        events.RunStageAsync(
            ProcessStage.StackNgCarrierJig,
            cancellationToken,
            async token =>
            {
                await PickAndPlaceAsync(
                    recipe.NgCarrierPickupPosition,
                    recipe.NgStackPosition,
                    token);
                await CarrierJigPositioner.CompleteRemovalAsync(token);

                NgStackCount++;
                events.NgStack(NgStackCount, NgStackCount >= NgStackCapacity);
            });

    private async Task PickAndPlaceAsync(
        AxisPos pickPosition,
        AxisPos placePosition,
        CancellationToken cancellationToken)
    {
        await motion.MoveToAsync(
            pickPosition.X,
            pickPosition.Y,
            pickPosition.Z,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.InspectionGripper,
            true,
            cancellationToken);

        await motion.MoveToAsync(
            placePosition.X,
            placePosition.Y,
            placePosition.Z,
            cancellationToken);

        await io.SetOutputAndWaitAsync(
            OutputIo.InspectionGripper,
            false,
            cancellationToken);
        await motion.MoveToSafeZAsync(cancellationToken);
    }
}
