using System;
using IBTM.Core;

namespace IBTM.BoltFeeder;

public sealed class BoltFeederSettings : Setting
{
    public int PickupTimeoutMilliseconds { get; set; } = 10_000;
    public int ShootingTimeoutMilliseconds { get; set; } = 10_000;
    public int ShootingRunOnMilliseconds
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 3_000;
}
