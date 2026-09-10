using System;
using IBTM.Core;

namespace IBTM.Conveyor;

public sealed class ConveyorSettings : Setting
{
    private double _carrierStopDelaySeconds = 1.0;

    public double CarrierStopDelaySeconds
    {
        get
        {
            return _carrierStopDelaySeconds;
        }
        set
        {
            if (!double.IsFinite(value)
                || value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Carrier stop delay must be a finite number of 0 seconds or more.");
            }

            _carrierStopDelaySeconds = value;
        }
    }
}
