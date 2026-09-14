using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Hantas;

public sealed class AdcBoltHead(IAdcBus bus, HantasSettings connection, byte slaveAddress) : IBoltHead
{
    private const int ResultPollMilliseconds = 50;
    // Connection edits apply to a newly created head, together with its slave address.
    private readonly string _portName = connection.PortName;
    private readonly int _baudRate = connection.BaudRate;
    // Requested conditions and result ownership; never a substitute for controller feedback.
    private ushort? _requestedPreset;
    private (ushort EventCount, ushort Preset)? _pendingFastening;

    public bool HasPendingResult
    {
        get
        {
            return _pendingFastening is not null;
        }
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bus.Open(_portName, _baudRate);
        await bus.ReadDeviceInformationAsync(slaveAddress, cancellationToken);
        var status = await bus.ReadControllerStatusAsync(slaveAddress, cancellationToken);
        RequireReady(status);
    }

    private void RequireReady(AdcControllerStatus status)
    {
        if (status.Alarm != 0)
            throw new InvalidOperationException($"ADC {slaveAddress} controller error: {status.Alarm}.");
        if (!status.Ready || status.Running)
            throw new InvalidOperationException(
                $"ADC {slaveAddress} must be ready and stopped before starting.");
    }

    public async Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
    {
        var current = await bus.ReadControllerStatusAsync(slaveAddress, cancellationToken);
        RequireReady(current);
        if (current.Preset != preset)
        {
            await bus.SelectPresetAsync(slaveAddress, preset, cancellationToken);
            current = await bus.ReadControllerStatusAsync(slaveAddress, cancellationToken);
            RequireReady(current);
            RequirePreset(current, preset);
        }
        _requestedPreset = preset;
    }

    private void RequirePreset(AdcControllerStatus status, ushort preset)
    {
        if (status.Preset != preset)
            throw new InvalidOperationException(
                $"ADC {slaveAddress} preset is {status.Preset}; this fastening requires preset {preset}.");
    }

    private void RequireDirection(AdcControllerStatus status, AdcDirection direction)
    {
        if (status.Direction != direction)
            throw new InvalidOperationException(
                $"ADC {slaveAddress} direction is {status.Direction}; expected {direction}.");
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bus.Open(_portName, _baudRate);
        await bus.StopAsync(slaveAddress, cancellationToken);
        var status = await WaitForStoppedAsync(cancellationToken);
        if (status.Alarm != 0)
        {
            await bus.ResetAlarmAsync(slaveAddress, cancellationToken);
        }

        // Do not discard an interrupted fastening result when clearing a hardware alarm.
        await CheckReadyAsync(cancellationToken);
    }

    // Manual hold-to-run only. Cancellation stops rotation; it is not a loose-complete result.
    public async Task RunReverseAsync(CancellationToken cancellationToken)
    {
        await CheckReadyAsync(cancellationToken);
        Exception? failure = null;
        try
        {
            await bus.SetDirectionAsync(slaveAddress, AdcDirection.Loosening, cancellationToken);
            var ready = await bus.ReadControllerStatusAsync(slaveAddress, cancellationToken);
            RequireReady(ready);
            RequireDirection(ready, AdcDirection.Loosening);
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
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            await StopAfterOperationAsync(failure);
        }
    }

    public async Task<BoltResult> TightenAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        AdcFasteningResult? completed = null;
        Exception? failure = null;
        try
        {
            var current = await bus.ReadFasteningResultAsync(slaveAddress, cancellationToken);
            if (current.Status == AdcEventStatus.Error
                || _pendingFastening is { } pending
                && IsCompleted(current, pending))
            {
                completed = current;
            }
            else
            {
                var status = await bus.ReadControllerStatusAsync(slaveAddress, cancellationToken);
                var fastening = _pendingFastening
                    ?? (EventCount: current.EventCount, Preset: _requestedPreset ?? status.Preset);
                RequireReady(status);
                RequirePreset(status, fastening.Preset);
                await bus.SetDirectionAsync(slaveAddress, AdcDirection.Fastening, cancellationToken);
                status = await bus.ReadControllerStatusAsync(slaveAddress, cancellationToken);
                RequireReady(status);
                RequirePreset(status, fastening.Preset);
                RequireDirection(status, AdcDirection.Fastening);

                timeout.CancelAfter(connection.FasteningTimeoutMilliseconds);
                timeout.Token.ThrowIfCancellationRequested();
                _pendingFastening = fastening;
                await bus.StartAsync(slaveAddress, timeout.Token);
                while (true)
                {
                    var result = await bus.ReadFasteningResultAsync(slaveAddress, timeout.Token);
                    if (IsCompleted(result, fastening))
                    {
                        completed = result;
                        break;
                    }

                    await Task.Delay(ResultPollMilliseconds, timeout.Token);
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            failure = exception;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            failure = new TimeoutException(
                $"ADC {slaveAddress} fastening timed out after {connection.FasteningTimeoutMilliseconds} ms.");
            throw failure;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            await StopAfterOperationAsync(failure);
        }

        if (completed is not null)
        {
            return Complete(completed);
        }

        // Stop is already sent. Collect a late result without restarting the head.
        return await ReadPendingResultAsync(CancellationToken.None)
            ?? throw new OperationCanceledException(cancellationToken);
    }

    public async Task<BoltResult?> ReadPendingResultAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingFastening is not { } pending)
        {
            return null;
        }

        var result = await bus.ReadFasteningResultAsync(slaveAddress, cancellationToken);
        if (!IsCompleted(result, pending))
            return null;
        // A previous STOP may have failed. Confirm RUN OFF before releasing ownership.
        // Once a result is received, finish this bounded check even if the operator cancels.
        await WaitForStoppedAsync(CancellationToken.None);
        return Complete(result);
    }

    public void DiscardPendingResult()
    {
        _pendingFastening = null;
        _requestedPreset = null;
    }

    private async Task StopAfterOperationAsync(Exception? failure)
    {
        try
        {
            await bus.StopAsync(slaveAddress, CancellationToken.None);
            await WaitForStoppedAsync(CancellationToken.None);
        }
        catch (Exception stopError) when (failure is not null)
        {
            throw new AggregateException("ADC operation and STOP both failed.", failure, stopError);
        }
    }

    private async Task<AdcControllerStatus> WaitForStoppedAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connection.ResponseTimeoutMilliseconds);
        try
        {
            while (true)
            {
                var status = await bus.ReadControllerStatusAsync(slaveAddress, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (!status.Running)
                    return status;
                await Task.Delay(ResultPollMilliseconds, timeout.Token);
            }
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"ADC {slaveAddress} motor stop was not confirmed within {connection.ResponseTimeoutMilliseconds} ms.",
                exception);
        }
    }

    private BoltResult Complete(AdcFasteningResult result)
    {
        _pendingFastening = null;
        if (result.Status == AdcEventStatus.Error)
        {
            throw new InvalidOperationException($"ADC {slaveAddress} controller error: {result.Error}.");
        }

        return new BoltResult(result.Status == AdcEventStatus.FasteningOk, result.Torque);
    }

    private bool IsCompleted(AdcFasteningResult result, (ushort EventCount, ushort Preset) pending)
    {
        if (result.Status == AdcEventStatus.Error)
            return true;
        if (result.EventCount == pending.EventCount
            || result.Status is not (AdcEventStatus.FasteningOk or AdcEventStatus.FasteningNg))
            return false;
        if (result.Preset != pending.Preset
            || result.Direction != AdcDirection.Fastening)
            throw new InvalidOperationException(
                $"ADC {slaveAddress} result event {result.EventCount} does not match this fastening: "
                + $"preset {result.Preset}, direction {result.Direction}; expected preset {pending.Preset}, Fastening.");
        return true;
    }

}
