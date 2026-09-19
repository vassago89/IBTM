using IBTM.Device;

namespace IBTM.BoltFeeder;

public sealed class PickupBoltFeeder : BoltFeeder
{
    private readonly BoltFeederSettings _settings;

    public PickupBoltFeeder(IIoService io, BoltFeederSettings settings)
        : base(io, InputIo.PickupFeederBoltDetected)
    {
        _settings = settings;
    }

    protected override int TimeoutMilliseconds => _settings.PickupTimeoutMilliseconds;
}
