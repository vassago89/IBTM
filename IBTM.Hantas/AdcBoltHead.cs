using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Hantas;

public sealed class AdcBoltHead(IAdcBus bus, HantasSettings connection, byte slaveAddress) : IBoltHead
{
    private const int ResultPollMilliseconds = 50;
    private ushort? _fasteningEvent;

    public BoltHeadState State
    {
        get
        {
            return _fasteningEvent is null ? BoltHeadState.Ready : BoltHeadState.Tightening;
        }
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bus.Open(connection.PortName, connection.BaudRate);
        await bus.ReadDeviceInformationAsync(slaveAddress, cancellationToken);
        var status = await bus.ReadControllerStatusAsync(slaveAddress, cancellationToken);
        if (status.Alarm != 0)
            throw new InvalidOperationException($"ADC {slaveAddress} controller error: {status.Alarm}.");
        if (!status.Ready || status.Running)
            throw new InvalidOperationException(
                $"ADC {slaveAddress} must be ready and stopped before starting.");
    }

    public async Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
    {
        var current = await bus.ReadControllerStatusAsync(slaveAddress, cancellationToken);
        if (current.Preset == preset)
        {
            return;
        }

        await bus.SelectPresetAsync(slaveAddress, preset, cancellationToken);
    }

    // Manual hold-to-run only. Cancellation stops rotation; it is not a loose-complete result.
    public async Task RunReverseAsync(CancellationToken cancellationToken)
    {
        await CheckReadyAsync(cancellationToken);
        try
        {
            await bus.SetDirectionAsync(slaveAddress, AdcDirection.Loosening, cancellationToken);
            await bus.StartAsync(slaveAddress, cancellationToken);
            while (true)
            {
                var status = await bus.ReadControllerStatusAsync(slaveAddress, cancellationToken);
                if (status.Alarm != 0)
                    throw new InvalidOperationException(
                        $"ADC {slaveAddress} controller error: {status.Alarm}.");
                await Task.Delay(ResultPollMilliseconds, cancellationToken);
            }
        }
        finally
        {
            await bus.StopAsync(slaveAddress, CancellationToken.None);
        }
    }

    public async Task<BoltResult> TightenAsync(CancellationToken cancellationToken = default)
    {
        AdcFasteningResult? completed = null;
        try
        {
            var current = await bus.ReadFasteningResultAsync(slaveAddress, cancellationToken);
            if (current.Status == AdcEventStatus.Error
                || _fasteningEvent is { } activeEvent
                && IsCompleted(current, activeEvent))
            {
                completed = current;
            }
            else
            {
                var previousEvent = _fasteningEvent ?? current.EventCount;
                await bus.SetDirectionAsync(slaveAddress, AdcDirection.Fastening, cancellationToken);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(connection.FasteningTimeoutMilliseconds);
                try
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    _fasteningEvent = previousEvent;
                    await bus.StartAsync(slaveAddress, timeout.Token);
                    while (true)
                    {
                        var result = await bus.ReadFasteningResultAsync(slaveAddress, timeout.Token);
                        if (IsCompleted(result, previousEvent))
                        {
                            completed = result;
                            break;
                        }

                        await Task.Delay(ResultPollMilliseconds, timeout.Token);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"ADC {slaveAddress} fastening timed out after {connection.FasteningTimeoutMilliseconds} ms.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await bus.StopAsync(slaveAddress, CancellationToken.None);
        }

        if (completed is null)
        {
            // Stop is already sent. Read a finished result once, without restarting the head.
            if (_fasteningEvent is { } previousEvent)
            {
                var result = await bus.ReadFasteningResultAsync(slaveAddress, CancellationToken.None);
                if (IsCompleted(result, previousEvent))
                {
                    return Complete(result);
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }

        return Complete(completed);
    }

    public void DiscardPendingResult()
    {
        _fasteningEvent = null;
    }

    private BoltResult Complete(AdcFasteningResult result)
    {
        _fasteningEvent = null;
        if (result.Status == AdcEventStatus.Error)
        {
            throw new InvalidOperationException($"ADC {slaveAddress} controller error: {result.Error}.");
        }

        return new BoltResult(result.Status == AdcEventStatus.FasteningOk, result.Torque);
    }

    private static bool IsCompleted(AdcFasteningResult result, ushort previousEvent)
    {
        return result.Status == AdcEventStatus.Error
            || result.EventCount != previousEvent
            && result.Status is AdcEventStatus.FasteningOk or AdcEventStatus.FasteningNg;
    }

}
