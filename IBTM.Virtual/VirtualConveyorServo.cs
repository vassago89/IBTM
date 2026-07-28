using System;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualConveyorServo(VirtualIoService io) : IConveyorServo
{
    private bool _initialized;

    public event Action<bool>? RunningChanged;

    public bool IsRunning { get; private set; }

    public void Initialize()
    {
        _initialized = true;
    }

    public void Run(double _)
    {
        IsRunning = true;
        io.StartConveyor();
        RunningChanged?.Invoke(true);
    }

    public void Stop()
    {
        IsRunning = false;
        io.StopConveyor();
        RunningChanged?.Invoke(false);
    }

    public void EmergencyStop() => Stop();

    public void SetServo(bool on) => _initialized = on;

    public AxisState GetAxisState() => new(
        Homed: true,
        ServoOn: _initialized,
        Alarm: false,
        InPosition: !IsRunning,
        Emergency: false,
        HomeSensor: false,
        PositiveLimit: false,
        NegativeLimit: false);

    public void ResetAlarm()
    {
    }
}
