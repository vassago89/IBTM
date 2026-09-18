using System;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementWork : StationWork
{
    public PcbPlacementWork(ConveyorStation station, Func<bool>? isEnabled = null)
        : base(station, isEnabled)
    {
    }
}
