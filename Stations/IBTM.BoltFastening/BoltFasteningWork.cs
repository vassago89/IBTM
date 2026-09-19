using System;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningWork : StationWork
{
    public BoltFasteningWork(ConveyorStation station, Func<bool>? isEnabled = null)
        : base(station, isEnabled)
    {
    }

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
