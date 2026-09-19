using System;
using IBTM.Core;

namespace IBTM.Conveyor;

public sealed class ConveyorSettings : Setting
{
    public double TransferTimeoutSeconds
    {
        get;
        set
        {
            if (!double.IsFinite(value)
                || value <= 0
                || value > int.MaxValue / 1000.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Transfer timeout must be positive and fit in milliseconds.");
            }

            field = value;
        }
    } = 5.0;

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
