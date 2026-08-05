using System;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualConveyorServo(VirtualIoService io) : IConveyorServo
{
    private bool _initialized;
    private bool _isRunning;

    public event Action<bool>? RunningChanged;

    public void Initialize()
    {
        _initialized = true;
    }

    public void Run(double _)
    {
        _isRunning = true;
        io.StartConveyor();
        RunningChanged?.Invoke(true);
    }

    public void Stop()
    {
        _isRunning = false;
        io.StopConveyor();
        RunningChanged?.Invoke(false);
    }

    public void EmergencyStop() => Stop();

    public void SetServo(bool on) => _initialized = on;

    public AxisState GetAxisState() => new(
        Homed: true,
        ServoOn: _initialized,
        Alarm: false,
        InPosition: !_isRunning,
        Emergency: false,
        HomeSensor: false,
        PositiveLimit: false,
        NegativeLimit: false);

    public void ResetAlarm()
    {
    }
}
