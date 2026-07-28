using System.Collections.Generic;
using IBTM.Device;

namespace IBTM.Virtual;

public sealed class VirtualLightController : ILightController
{
    public HashSet<int> ActiveChannels { get; } = [];

    public void Initialize()
    {
    }

    public void SetLevel(int channel, int level)
    {
    }

    public void TurnOn(int channel) =>
        ActiveChannels.Add(channel);

    public void TurnOff(int channel) =>
        ActiveChannels.Remove(channel);

    public void TurnOffAll() =>
        ActiveChannels.Clear();
}
