using IBTM.Core;

namespace IBTM.BoltFeeder;

public sealed class BoltFeederSettings : Setting
{
    public int PickupTimeoutMilliseconds { get; set; } = 10_000;
    public int LinearTimeoutMilliseconds { get; set; } = 10_000;
}
