using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.BoltFastening;

public sealed partial class BoltFasteningStation
{
    private bool _repeat;

    private async Task RepeatCarrierAsync(CancellationToken cancellationToken)
    {
        if (!_units.MainConveyor && _work.Completed)
        {
            if (!_work.Station.CarrierSeated || !IsHorizontalMoveAllowed || !IsAtSafeZ())
                throw new InvalidOperationException("Fastening repeat requires the original seated carrier and both heads at safe height.");
            _work.StartRepeat(_work.CurrentJob);
        }
        await RunCarrierAsync(cancellationToken);
    }
}
