using System;
using IBTM.Core;

namespace IBTM.NgConveyor;

public sealed class NgConveyorSettings : Setting
{
    public int AlarmCarrierCount
    {
        get;
        set
        {
            if (value is < 1 or > 3)
                throw new ArgumentOutOfRangeException(nameof(value), "Use an NG alarm carrier count from 1 to 3.");
            field = value;
        }
    } = 3;

    public double CarrierStopDelaySeconds
    {
        get;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value * 1000 > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(value), "Use a finite NG conveyor stop delay of 0 seconds or more.");
            field = value;
        }
    } = 5;

    public double EjectRunSeconds
    {
        get;
        set
        {
            if (!double.IsFinite(value) || value <= 0 || value * 1000 > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(value), "Use a positive, finite NG ejection time in seconds.");
            field = value;
        }
    } = 5;
}
