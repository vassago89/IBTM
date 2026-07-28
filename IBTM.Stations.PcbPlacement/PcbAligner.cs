using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Stations.PcbPlacement;

public sealed class PcbAligner(
    MotionService motion,
    PcbPlacementSettings settings,
    ICameraStreamService camera,
    ILightController light,
    LightingSettings lighting)
{
    public void Initialize() =>
        camera.Initialize();

    public async Task<FiducialResult?> AlignFromFirstFiducialAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken)
    {
        var first = await MeasureCurrentAsync(cancellationToken);
        if (first is null)
        {
            return null;
        }

        var second = await MeasureAsync(
            recipe.Fiducial2Position,
            cancellationToken);
        if (second is null)
        {
            return null;
        }

        return new FiducialResult(
            (first.OffsetX + second.OffsetX) / 2,
            (first.OffsetY + second.OffsetY) / 2);
    }

    private async Task<FiducialResult?> MeasureAsync(
        AxisPos position,
        CancellationToken cancellationToken)
    {
        await motion.MoveToAsync(
            position.X,
            position.Y,
            position.Z,
            cancellationToken);

        return await MeasureCurrentAsync(cancellationToken);
    }

    private async Task<FiducialResult?> MeasureCurrentAsync(
        CancellationToken cancellationToken)
    {
        light.SetLevel(lighting.AlignmentChannel, lighting.AlignmentLevel);
        light.TurnOn(lighting.AlignmentChannel);
        try
        {
            return Detect(camera.Capture());
        }
        finally
        {
            light.TurnOff(lighting.AlignmentChannel);
            await motion.MoveToSafeZAsync(cancellationToken);
        }
    }

    private FiducialResult? Detect(ImageFrame image)
    {
        long xTotal = 0;
        long yTotal = 0;
        var count = 0;

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var index = (y * image.Stride) + (x * 3);
                var blue = image.Pixels[index];
                var green = image.Pixels[index + 1];
                var red = image.Pixels[index + 2];
                if (green <= blue + 50 || green <= red + 50)
                {
                    continue;
                }

                xTotal += x;
                yTotal += y;
                count++;
            }
        }

        return count == 0
            ? null
            : new FiducialResult(
                ((xTotal / (double)count) - (image.Width / 2.0))
                * settings.AlignmentXMillimetersPerPixel,
                ((yTotal / (double)count) - (image.Height / 2.0))
                * settings.AlignmentYMillimetersPerPixel);
    }
}
