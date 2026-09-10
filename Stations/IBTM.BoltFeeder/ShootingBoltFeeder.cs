using IBTM.Device;

namespace IBTM.BoltFeeder;

public sealed class ShootingBoltFeeder(IIoService io, BoltFeederSettings settings) : BoltFeeder(
    io,
    InputIo.ShootingFeederBoltDetected)
{
    protected override int TimeoutMilliseconds
    {
        get
        {
            return settings.ShootingTimeoutMilliseconds;
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
