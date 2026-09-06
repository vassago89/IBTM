using System.Collections.Generic;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementWork(ConveyorStation station, bool enabled = true)
    : StationWork(station, enabled)
{
    public void PrepareRecovery(
        IEnumerable<(HeatSinkSlot HeatSink, bool Completed)> items)
    {
        foreach (var (heatSink, completed) in items)
        {
            if (completed)
            {
                Assembly(heatSink);
            }
            else
            {
                RemoveAssembly(heatSink);
            }
        }

        Restart();
    }
}
