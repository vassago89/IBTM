using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionWork : StationWork
{
    private readonly IIoService _io;

    public InspectionWork(IIoService io) : base(
        io,
        InputIo.InspectionCarrierJigPresent,
        InputIo.InspectionBackupPlateUp,
        InputIo.InspectionStopperDown,
        InputIo.InspectionHousing1Present,
        InputIo.InspectionHousing2Present)
    {
        _io = io;
        io.InputChanged += OnInputChanged;
    }

    public override bool CanReceive =>
        base.CanReceive
        && !_io.GetInput(InputIo.NgCarrierJigDetected);

    public override bool HasNg =>
        base.HasNg
        || Completed
        && !HousingPresent(HousingSlot.Housing1)
        && !HousingPresent(HousingSlot.Housing2);

    public InspectionState State =>
        !CarrierPresent
            ? InspectionState.WaitingForCarrier
            : !Ready
                ? InspectionState.WaitingForSeat
                : Completed
                    ? InspectionState.WaitingForTransfer
                    : InspectionState.ReadyToInspect;

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input == InputIo.NgCarrierJigDetected)
        {
            NotifyChanged();
        }
    }
}
