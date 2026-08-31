using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningWork(IIoService io) : StationWork(
    io,
    InputIo.BoltFasteningCarrierPresent,
    InputIo.BoltFasteningBackupPlateUp,
    InputIo.BoltFasteningStopperDown,
    InputIo.BoltFasteningHeatSink1Present,
    InputIo.BoltFasteningHeatSink2Present)
{
    private static readonly BoltResult ManualCompletion =
        new(true, 0, BoltResultSource.Manual);

    public BoltFasteningState State =>
        !CarrierPresent
            ? BoltFasteningState.WaitingForCarrier
            : Completed
                ? BoltFasteningState.WaitingForTransfer
                : !Ready
                    ? BoltFasteningState.WaitingForSeat
                    : BoltFasteningState.ReadyToFasten;

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
