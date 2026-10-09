using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public interface IAxisMotion : IMotionFeedback
{
    void Initialize();
    void Stop();
    Task MoveAxisAsync(
        MotionAxis axis,
        double position,
        double velocity,
        CancellationToken cancellationToken = default,
        double? accelerationSeconds = null,
        double? decelerationSeconds = null);
    Task<bool> HomeAsync(MotionAxis axis, double velocity, CancellationToken cancellationToken = default);
    Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default);
    // Reset drive alarms only. The machine sequence owns servo enablement.
    Task ResetAsync(CancellationToken cancellationToken = default);
    void SetServo(MotionAxis axis, bool on);
}
