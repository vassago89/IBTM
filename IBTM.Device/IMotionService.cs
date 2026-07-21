using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Device;

public interface IMotionService
{
    event Action<double, double, double>? PositionChanged;

    void Initialize();
    Task MoveToXYAsync(
        double x,
        double y,
        double velocity,
        CancellationToken cancellationToken = default);
    Task MoveToZAsync(
        double z,
        double velocity,
        CancellationToken cancellationToken = default);
    void JogX(double velocity);
    void JogY(double velocity);
    void JogZ(double velocity);
    void Stop();
    void EmergencyStop();
    (double X, double Y, double Z) GetPosition();
}
