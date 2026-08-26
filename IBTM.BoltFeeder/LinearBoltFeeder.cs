using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.BoltFeeder;

public sealed class LinearBoltFeeder(
    IIoService io,
    BoltFeederSettings settings)
{
    public BoltFeederState State =>
        io.GetInput(InputIo.LinearFeederBoltDetected)
            ? BoltFeederState.BoltReady
            : BoltFeederState.WaitingForBolt;

    public bool RunCommandOn =>
        io.GetOutput(OutputIo.LinearFeederRunSignal);

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (State == BoltFeederState.WaitingForBolt)
                {
                    io.SetOutput(OutputIo.LinearFeederRunSignal, true);
                    await io.WaitForInputAsync(
                        InputIo.LinearFeederBoltDetected,
                        true,
                        settings.LinearTimeoutMilliseconds,
                        cancellationToken);
                }

                io.SetOutput(OutputIo.LinearFeederRunSignal, false);
                await io.WaitForInputAsync(
                    InputIo.LinearFeederBoltDetected,
                    false,
                    Timeout.Infinite,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Stop();
        }
    }

    public Task WaitUntilReadyAsync(
        CancellationToken cancellationToken = default) =>
        io.WaitForInputAsync(
            InputIo.LinearFeederBoltDetected,
            true,
            Timeout.Infinite,
            cancellationToken);

    public void Stop() =>
        io.SetOutput(OutputIo.LinearFeederRunSignal, false);

}
