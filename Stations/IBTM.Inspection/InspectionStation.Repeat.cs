using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed partial class InspectionStation
{
    private async Task PrepareRepeatAsync(CancellationToken cancellationToken)
    {
        if ((!_transfer.IsEmptyRepeatAllowed && !_work.Station.CarrierPresent) || !_work.PickupClear)
            return;
        if (_work.Completed && !_units.NgCarrierTransfer && _work.Enabled)
            _work.StartRepeat(_work.CurrentJob);

        if (!_work.Enabled || _work.Completed && _units.NgCarrierTransfer)
        {
            if (_work.Station.BackupPlate != StationCylinderState.Up
                || _work.Station.Stopper != StationCylinderState.Down)
            {
                TraceStep(InspectionStationState.SeatingCarrier, workId: _work.CurrentJob.Id);
                await _transfer.SeatStationAsync(cancellationToken);
            }
        }
        else if (!_work.AtInspectionPosition)
        {
            await _work.Station.PrepareToReceiveAsync(cancellationToken);
        }
    }
}
