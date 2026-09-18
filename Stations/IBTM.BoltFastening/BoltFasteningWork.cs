using System;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningWork(ConveyorStation station, Func<bool>? isEnabled = null) : StationWork(
    station,
    isEnabled)
{
    internal BoltFasteningWorkState State
    {
        get
        {
            if (!CarrierPresent)
            {
                return BoltFasteningWorkState.WaitingForCarrier;
            }

            if (Completed)
            {
                return BoltFasteningWorkState.WaitingForTransfer;
            }

            return CarrierSeated
                ? BoltFasteningWorkState.ReadyToFasten
                : BoltFasteningWorkState.WaitingForSeat;
        }
    }

}
