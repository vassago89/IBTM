namespace IBTM.Device;

public interface IConveyorServo
{
    void Initialize();
    void Run(double velocity);
    void Stop();
    void EmergencyStop();
}
