namespace IBTM.Device.Abstractions;

/// <summary>
/// Controls one three-axis motion group. All positions and velocities use millimetres.
/// Absolute move methods complete only after the requested position has been reached.
/// </summary>
public interface IMotionService
{
    event EventHandler<MotionPositionEventArgs>? PositionChanged;

    void InitializeAxes(int? axisX, int? axisY, int? axisZ);
    void Enable();

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

    MotionPosition GetPosition();
}
