using System;
using AnyWave.Device.LightControllers;

namespace IBTM.Device;

// Connect the original AnyWave controller to IBTM's inspection and settings screens.
public sealed class MovsLightController : ILightController, IDisposable
{
    private readonly MOVSService _controller;
    private readonly string _portName;

    public MovsLightController(LightingSettings settings)
    {
        _controller = new MOVSService();
        _portName = settings.Connection;
    }

    public void Initialize()
    {
        _controller.Connect(_portName);
    }

    public void SetLevel(int channel, int level)
    {
        _controller.Set(channel, level);
    }

    public void TurnOn(int channel)
    {
        _controller.On(channel);
    }

    public void TurnOff(int channel)
    {
        _controller.Off(channel);
    }

    public void TurnOffAll()
    {
        _controller.Off();
    }

    public void Dispose()
    {
        _controller.Disconnect();
    }
}
