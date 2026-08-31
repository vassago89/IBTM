using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class BoltImageCapture(
    IXyMotion motion,
    ICamera camera,
    ILightController light,
    InspectionGantrySettings gantrySettings,
    CarrierReferenceSettings carrierReference,
    LightingSettings lightingSettings)
{
    private const double PositionTolerance = 0.05;

    public bool HasPosition(BoltPoint point) =>
        CarrierCoordinates.IsDefined(
            carrierReference.UpperLeftPin,
            carrierReference.LowerRightPin)
        && point is { X: not null, Y: not null };

    public bool IsAt(BoltPoint point)
    {
        var current = motion.GetPosition();
        var target = gantrySettings.GetBoltPosition(
            point,
            carrierReference);
        return !motion.IsMoving
            && motion.GetAxisState(MotionAxis.X).InPosition
            && motion.GetAxisState(MotionAxis.Y).InPosition
            && System.Math.Abs(current.X - target.X) <= PositionTolerance
            && System.Math.Abs(current.Y - target.Y) <= PositionTolerance;
    }

    public Task MoveToAsync(
        BoltPoint point,
        CancellationToken cancellationToken = default)
    {
        var position = gantrySettings.GetBoltPosition(
            point,
            carrierReference);
        return motion.MoveToXYAsync(
            position.X,
            position.Y,
            gantrySettings.Motion.HorizontalSpeed,
            cancellationToken);
    }

    public ImageFrame Capture()
    {
        var channel = lightingSettings.InspectionChannel;
        light.SetLevel(channel, lightingSettings.InspectionLevel);
        light.TurnOn(channel);
        try
        {
            return camera.Capture();
        }
        finally
        {
            light.TurnOff(channel);
        }
    }

    public async Task<ImageFrame> CaptureAsync(
        BoltPoint point,
        CancellationToken cancellationToken = default)
    {
        await MoveToAsync(point, cancellationToken);
        return Capture();
    }
}
