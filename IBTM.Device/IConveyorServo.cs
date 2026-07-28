using System;

namespace IBTM.Device;

public interface IConveyorServo
{
    event Action<bool> RunningChanged;

    void Initialize();
    void Run(double velocity);
    void Stop();
    void EmergencyStop();
    void SetServo(bool on);
    AxisState GetAxisState();
    void ResetAlarm();
}
