using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Hantas;

public sealed class AdcBoltHead(
    IAdcBus bus,
    HantasSettings connection,
    byte slaveAddress) : IBoltHead
{
    private const int ResultPollMilliseconds = 50;
    private ushort? _fasteningEvent;

    public BoltHeadState State => _fasteningEvent is null
        ? BoltHeadState.Ready
        : BoltHeadState.Tightening;

    public async Task CheckReadyAsync(
        CancellationToken cancellationToken = default)
    {
        bus.Open(connection.PortName, connection.BaudRate);
        await bus.ReadDeviceInformationAsync(
            slaveAddress,
            cancellationToken);
        ThrowIfControllerError(await bus.ReadFasteningResultAsync(
            slaveAddress,
            cancellationToken));
    }

    public async Task SelectPresetAsync(
        ushort preset,
        CancellationToken cancellationToken = default)
    {
        var current = await bus.ReadFasteningResultAsync(
            slaveAddress,
            cancellationToken);
        if (current.Preset == preset)
        {
            return;
        }

        await bus.SelectPresetAsync(
            slaveAddress,
            preset,
            cancellationToken);
    }

    public async Task<BoltResult> TightenAsync(
        CancellationToken cancellationToken = default)
    {
        AdcFasteningResult? completed = null;
        try
        {
            var current = await bus.ReadFasteningResultAsync(
                slaveAddress,
                cancellationToken);
            if (current.Status == AdcEventStatus.Error
                || _fasteningEvent is { } activeEvent
                && IsCompleted(current, activeEvent))
            {
                completed = current;
            }
            else
            {
                var previousEvent = _fasteningEvent ?? current.EventCount;
                _fasteningEvent = previousEvent;
                await bus.SetDirectionAsync(
                    slaveAddress,
                    AdcDirection.Fastening,
                    cancellationToken);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                timeout.CancelAfter(connection.FasteningTimeoutMilliseconds);
                try
                {
                    await bus.StartAsync(slaveAddress, timeout.Token);
                    while (true)
                    {
                        var result = await bus.ReadFasteningResultAsync(
                            slaveAddress,
                            timeout.Token);
                        if (IsCompleted(result, previousEvent))
                        {
                            completed = result;
                            break;
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
                        + $"{connection.FasteningTimeoutMilliseconds} ms.");
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await bus.StopAsync(
                slaveAddress,
                CancellationToken.None);
        }

        if (completed is null)
        {
            // Stop is already sent. Read a finished result once, without restarting the head.
            if (_fasteningEvent is { } previousEvent)
            {
                var result = await bus.ReadFasteningResultAsync(
                    slaveAddress,
                    CancellationToken.None);
                if (IsCompleted(result, previousEvent))
                {
                    return Complete(result);
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }

        return Complete(completed);
    }

    public void DiscardPendingResult() => _fasteningEvent = null;

    private BoltResult Complete(AdcFasteningResult result)
    {
        _fasteningEvent = null;
        ThrowIfControllerError(result);
        return new BoltResult(
            result.Status == AdcEventStatus.FasteningOk,
            result.Torque);
    }

    private static bool IsCompleted(
        AdcFasteningResult result,
        ushort previousEvent) =>
        result.Status == AdcEventStatus.Error
        || result.EventCount != previousEvent
        && result.Status is AdcEventStatus.FasteningOk
            or AdcEventStatus.FasteningNg;

    private void ThrowIfControllerError(AdcFasteningResult result)
    {
        if (result.Status == AdcEventStatus.Error)
        {
            throw new InvalidOperationException(
                $"ADC {slaveAddress} controller error: {result.Error}.");
        }
    }
}
