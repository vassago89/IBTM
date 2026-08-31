using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.BoltFeeder;

public sealed class PickupBoltFeeder(
    IIoService io,
    BoltFeederSettings settings)
{
    public BoltFeederState State =>
        io.GetInput(InputIo.PickupFeederBoltDetected)
            ? BoltFeederState.BoltReady
            : BoltFeederState.WaitingForBolt;

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                switch (State)
                {
                    case BoltFeederState.WaitingForBolt:
                        await io.WaitForInputAsync(
                            InputIo.PickupFeederBoltDetected,
                            true,
                            settings.PickupTimeoutMilliseconds,
                            cancellationToken);
                        break;

                    case BoltFeederState.BoltReady:
                        await io.WaitForInputAsync(
                            InputIo.PickupFeederBoltDetected,
                            false,
                            Timeout.Infinite,
                            cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public Task WaitUntilReadyAsync(
        CancellationToken cancellationToken = default) =>
        io.WaitForInputAsync(
            InputIo.PickupFeederBoltDetected,
            true,
            Timeout.Infinite,
            cancellationToken);
}
