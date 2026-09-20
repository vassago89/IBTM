using System.Threading;
using System.Threading.Tasks;

namespace IBTM.Inspection;

public sealed partial class InspectionStation
{
    private async Task PrepareRepeatAsync(CancellationToken cancellationToken)
    {
        if (!_work.Station.CarrierPresent || !_work.PickupClear)
            return;
        if (_work.Completed && !_units.NgCarrierTransfer && _work.Enabled)
            _work.StartRepeat(_work.CurrentJob);

        if (!_work.Enabled || _work.Completed && _units.NgCarrierTransfer)
        {
            if (!_work.Station.CarrierSeated)
                await _work.Station.SeatAsync(cancellationToken);
        }
        else if (!_work.AtInspectionPosition)
        {
            await _work.Station.PrepareToReceiveAsync(cancellationToken);
        }
    }
}
