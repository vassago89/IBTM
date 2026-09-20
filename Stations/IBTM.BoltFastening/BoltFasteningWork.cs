using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningWork : StationWork
{
    public BoltFasteningWork(ConveyorStation station, UnitSettings units)
        : base(station, units)
    {
    }

    public override bool Enabled => Units.BoltFastening;

    internal bool IsReadyToFasten => Station.CarrierSeated && !Completed;
}
