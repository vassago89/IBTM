using IBTM.Device;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningWork(IIoService io) : StationWork(
    io,
    InputIo.BoltFasteningCarrierJigPresent,
    InputIo.BoltFasteningBackupPlateUp,
    InputIo.BoltFasteningStopperDown,
    InputIo.BoltFasteningHousing1Present,
    InputIo.BoltFasteningHousing2Present)
{
    public BoltFasteningState State =>
        !CarrierPresent
            ? BoltFasteningState.WaitingForCarrier
            : Completed
                ? BoltFasteningState.WaitingForTransfer
                : !Ready
                    ? BoltFasteningState.WaitingForSeat
                    : BoltFasteningState.ReadyToFasten;
}
