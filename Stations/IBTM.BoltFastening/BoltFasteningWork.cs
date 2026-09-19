using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningWork : StationWork
{
    public BoltFasteningWork(ConveyorStation station, UnitSettings units)
        : base(station, units)
    {
    }

    public override bool Enabled => Units.BoltFastening;

    internal BoltFasteningWorkState State
    {
        get
        {
            switch (true)
            {
                case true when !Station.CarrierPresent:
                    return BoltFasteningWorkState.WaitingForCarrier;
                case true when Completed:
                    return BoltFasteningWorkState.WaitingForTransfer;
                default:
                    return Station.CarrierSeated
                        ? BoltFasteningWorkState.ReadyToFasten
                        : BoltFasteningWorkState.WaitingForSeat;
            }
        }
    }
}
