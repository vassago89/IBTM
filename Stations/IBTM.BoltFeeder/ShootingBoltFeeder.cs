using IBTM.Device;

namespace IBTM.BoltFeeder;

public sealed class ShootingBoltFeeder : BoltFeeder
{
    private readonly BoltFeederSettings _settings;

    public ShootingBoltFeeder(IIoService io, BoltFeederSettings settings)
        : base(io, InputIo.ShootingFeederBoltDetected)
    {
        _settings = settings;
    }

    protected override int TimeoutMilliseconds
    {
        get
        {
            return _settings.ShootingTimeoutMilliseconds;
        }
    }

    public void Stop()
    {
        SetFeeding(false);
    }

    protected override void SetFeeding(bool value)
    {
        Io.SetOutput(OutputIo.ShootingFeederRunSignal, value);
    }
}
