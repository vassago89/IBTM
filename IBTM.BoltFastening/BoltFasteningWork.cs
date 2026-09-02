using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningWork(ConveyorStation station)
    : StationWork(station)
{
    private static readonly BoltResult ManualCompletion =
        new(true, 0, BoltResultSource.Manual);

    public BoltFasteningState State
    {
        get
        {
            if (!CarrierPresent)
            {
                return BoltFasteningState.WaitingForCarrier;
            }

            if (Completed)
            {
                return BoltFasteningState.WaitingForTransfer;
            }

            return CarrierSeated
                ? BoltFasteningState.ReadyToFasten
                : BoltFasteningState.WaitingForSeat;
        }
    }

    public void PrepareRecovery(
        IEnumerable<(HeatSinkSlot HeatSink, int Number, FasteningPass Pass)>
            completed)
    {
        foreach (var assembly in Assemblies)
        {
            assembly.ResetFastening();
        }

        foreach (var heatSink in System.Enum.GetValues<HeatSinkSlot>()
                     .Where(HeatSinkPresent))
        {
            _ = Assembly(heatSink);
        }

        foreach (var bolt in completed)
        {
            var assembly = Assembly(bolt.HeatSink);
            switch (bolt.Pass)
            {
                case FasteningPass.Pcb:
                    assembly.RecordPcbBolt(bolt.Number, ManualCompletion);
                    break;
                case FasteningPass.IpmSeating:
                    assembly.RecordIpmSeating(
                        bolt.Number,
                        ManualCompletion);
                    break;
                case FasteningPass.IpmFinal:
                    assembly.RecordIpmFinal(
                        bolt.Number,
                        ManualCompletion);
                    break;
            }
        }

        Restart();
    }
}
