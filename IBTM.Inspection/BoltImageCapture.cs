using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed record BoltImage(BoltPoint Point, ImageFrame Image);

public sealed class BoltImageCapture(
    IXyMotion motion,
    ICamera camera,
    ILightController light,
    InspectionGantrySettings gantrySettings,
    LightingSettings lightingSettings)
{
    public async Task<IReadOnlyList<BoltImage>> CaptureAsync(
        IEnumerable<BoltPoint> points,
        CancellationToken cancellationToken = default)
    {
        var images = new List<BoltImage>();
        var channel = lightingSettings.InspectionChannel;
        light.SetLevel(channel, lightingSettings.InspectionLevel);
        light.TurnOn(channel);
        try
        {
            foreach (var point in points.OrderBy(point => point.Number))
            {
                var position = gantrySettings.GetBoltPosition(point);
                await motion.MoveToXYAsync(
                    position.X,
                    position.Y,
                    gantrySettings.Motion.HorizontalSpeed,
                    cancellationToken);
                images.Add(new BoltImage(point, camera.Capture()));
            }
        }
        finally
        {
            light.TurnOff(channel);
        }

        return images;
    }
}
