using System;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementWork(ConveyorStation station, Func<bool>? isEnabled = null) : StationWork(
    station,
    isEnabled)
{
}
