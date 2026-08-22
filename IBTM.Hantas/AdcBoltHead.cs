using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Hantas;

public sealed class AdcBoltHead(
    AdcBus bus,
    HantasSettings connection,
    byte slaveAddress) : IBoltHead
{
    private const int FasteningTimeoutMilliseconds = 15_000;
    private const int ResultPollMilliseconds = 50;

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        bus.Open(connection.PortName, connection.BaudRate);
        await bus.ReadDeviceInformationAsync(
            slaveAddress,
            cancellationToken);
    }

    public async Task<BoltResult> TightenAsync(
        ushort preset,
        CancellationToken cancellationToken = default)
    {
        await bus.SelectPresetAsync(
            slaveAddress,
            preset,
            cancellationToken);
        await bus.SetDirectionAsync(
            slaveAddress,
            AdcDirection.Fastening,
            cancellationToken);
        var previousEvent = (await bus.ReadFasteningResultAsync(
            slaveAddress,
            cancellationToken)).EventCount;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(FasteningTimeoutMilliseconds);

        try
        {
            await bus.StartAsync(slaveAddress, timeout.Token);
            while (true)
            {
                var result = await bus.ReadFasteningResultAsync(
                    slaveAddress,
                    timeout.Token);
                if (result.EventCount != previousEvent
                    && result.Status is AdcEventStatus.FasteningOk
                        or AdcEventStatus.FasteningNg
                        or AdcEventStatus.Error)
                {
                    return new BoltResult(
                        result.Status == AdcEventStatus.FasteningOk,
                        result.Torque);
                }

                await Task.Delay(
                    ResultPollMilliseconds,
                    timeout.Token);
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"ADC fastening timed out after "
                + $"{FasteningTimeoutMilliseconds} ms.");
        }
        finally
        {
            await bus.StopAsync(
                slaveAddress,
                CancellationToken.None);
        }
    }
}
