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
            if (!Station.CarrierPresent)
            {
                return BoltFasteningWorkState.WaitingForCarrier;
            }

            if (Completed)
            {
                return BoltFasteningWorkState.WaitingForTransfer;
            }

            return Station.CarrierSeated
                ? BoltFasteningWorkState.ReadyToFasten
                : BoltFasteningWorkState.WaitingForSeat;
        }
    }
}
