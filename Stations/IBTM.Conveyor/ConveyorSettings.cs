using System;
using IBTM.Core;

namespace IBTM.Conveyor;

public sealed class ConveyorSettings : Setting
{
    public double CarrierStopDelaySeconds
    {
        get;
        set
        {
            if (!double.IsFinite(value)
                || value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Carrier stop delay must be a finite number of 0 seconds or more.");
            }

            field = value;
        }
    } = 3.0;

    public double ExitSensorClearDelaySeconds
    {
        get;
        set
        {
            if (!double.IsFinite(value)
                || value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Exit sensor clear delay must be a finite number of 0 seconds or more.");
            }

            field = value;
        }
    } = 0.3;
}
