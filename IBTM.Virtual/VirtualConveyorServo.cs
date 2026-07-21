using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualConveyorServo(VirtualIoService io) : IConveyorServo
{
    public bool IsRunning { get; private set; }
    public double Velocity { get; private set; }

    public void Initialize()
    {
    }

    public void Run(double velocity)
    {
        Velocity = velocity;
        IsRunning = true;
        io.StartConveyor();
    }

    public void Stop()
    {
        IsRunning = false;
        Velocity = 0;
        io.StopConveyor();
    }

    public void EmergencyStop() => Stop();
}
