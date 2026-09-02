using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementWork(ConveyorStation station)
    : StationWork(station)
{
    public void PrepareRecovery(IEnumerable<HeatSinkSlot> completed) =>
        SetAssemblies(completed.Select(heatSink =>
            new HeatSinkAssembly(heatSink)));
}
