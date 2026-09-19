using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementWork : StationWork
{
    public PcbPlacementWork(ConveyorStation station, UnitSettings units)
        : base(station, units)
    {
    }

    public override bool Enabled => Units.PcbPlacement;
}
