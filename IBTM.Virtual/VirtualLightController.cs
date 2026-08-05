using System.Collections.Generic;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualLightController : ILightController
{
    private readonly HashSet<int> _activeChannels = [];

    public void Initialize()
    {
    }

    public void SetLevel(int channel, int level)
    {
    }

    public void TurnOn(int channel) =>
        _activeChannels.Add(channel);

    public void TurnOff(int channel) =>
        _activeChannels.Remove(channel);

    public void TurnOffAll() =>
        _activeChannels.Clear();
}
