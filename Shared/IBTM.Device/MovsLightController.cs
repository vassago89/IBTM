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
        if (string.IsNullOrWhiteSpace(_portName))
            throw new InvalidOperationException("Set the MOVS lighting COM port before using the light controller.");
        _controller.Connect(_portName);
    }

    public void SetLevel(int channel, int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 9);
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 255);
        _controller.Set(channel, level);
    }

    public void TurnOn(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 9);
        _controller.On(channel);
    }

    public void TurnOff(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 9);
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
