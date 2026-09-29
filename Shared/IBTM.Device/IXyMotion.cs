using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public interface IXyMotion : IAxisMotion
{
    Task AdjustAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default);
    Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default);
    Task<bool> HomeHorizontalAsync(double velocity, CancellationToken cancellationToken = default);
}
