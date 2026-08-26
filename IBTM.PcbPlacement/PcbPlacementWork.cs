using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementWork(IIoService io) : StationWork(
    io,
    InputIo.PcbPlacementCarrierJigPresent,
    InputIo.PcbPlacementBackupPlateUp,
    InputIo.PcbPlacementStopperDown,
    InputIo.PcbPlacementHousing1Present,
    InputIo.PcbPlacementHousing2Present)
{ }
