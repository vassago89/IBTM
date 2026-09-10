using System;
using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
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

    public void PrepareRecovery(
        IEnumerable<(HeatSinkSlot HeatSink, int Number, FasteningPass Pass, bool Completed)> items)
    {
        foreach (var group in items.GroupBy(item => item.HeatSink))
        {
            Assembly(group.Key)
                .PrepareFasteningRecovery(
                    group.Where(item => item.Pass == FasteningPass.Pcb)
                        .Select(item => (item.Number, item.Completed)),
                    group.Where(item => item.Pass == FasteningPass.IpmSeating)
                        .Select(item => (item.Number, item.Completed)),
                    group.Where(item => item.Pass == FasteningPass.IpmFinal)
                        .Select(item => (item.Number, item.Completed)));
        }

        Restart();
    }
}
