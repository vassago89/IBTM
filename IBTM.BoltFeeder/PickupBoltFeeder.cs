using IBTM.Device;

namespace IBTM.BoltFeeder;

public sealed class PickupBoltFeeder(IIoService io, BoltFeederSettings settings) : BoltFeeder(
    io,
    InputIo.PickupFeederBoltDetected)
{
    protected override int TimeoutMilliseconds
    {
        get
        {
            return settings.PickupTimeoutMilliseconds;
        }
    }
}
