using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementWork(IIoService io) : StationWork(
    io,
    InputIo.PcbPlacementCarrierPresent,
    InputIo.PcbPlacementBackupPlateUp,
    InputIo.PcbPlacementStopperDown,
    InputIo.PcbPlacementHeatSink1Present,
    InputIo.PcbPlacementHeatSink2Present)
{
    public void PrepareRecovery(IEnumerable<HeatSinkSlot> completed) =>
        SetAssemblies(completed.Select(heatSink =>
            new PcbAssembly(heatSink)));
}
