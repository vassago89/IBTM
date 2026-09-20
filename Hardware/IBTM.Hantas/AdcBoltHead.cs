using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Hantas;

public sealed class AdcBoltHead : IBoltHead
{
    private readonly IAdcBus _bus;
    private readonly HantasSettings _connection;
    private readonly byte _slaveAddress;
    private const int ResultPollMilliseconds = 50;
    // Connection edits apply to a newly created head, together with its slave address.
    private readonly string _portName;
    private readonly int _baudRate;
    // Requested conditions and result ownership; never a substitute for controller feedback.
    private ushort? _requestedPreset;
    private (ushort EventCount, ushort Preset)? _pendingFastening;
    private bool _feedUnconfirmed;

    public AdcBoltHead(
        IAdcBus bus,
        HantasSettings connection,
        byte slaveAddress,
        string portName,
        int baudRate)
    {
        _bus = bus;
        _connection = connection;
        _slaveAddress = slaveAddress;
        _portName = portName;
        _baudRate = baudRate;
    }

    public bool HasPendingResult => _pendingFastening is not null;

    public async Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _bus.Open(_portName, _baudRate);
        await _bus.ReadDeviceInformationAsync(_slaveAddress, cancellationToken);
        var status = await _bus.ReadControllerStatusAsync(_slaveAddress, cancellationToken);
        RequireReady(status);
    }

    private void RequireReady(AdcControllerStatus status)
    {
        if (status.Alarm != 0)
            throw new InvalidOperationException($"ADC {_slaveAddress} controller error: {status.Alarm}.");
        if (!status.Ready || status.Running)
            throw new InvalidOperationException(
                $"ADC {_slaveAddress} must be ready and stopped before starting.");
    }

    public async Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
    {
        var current = await _bus.ReadControllerStatusAsync(_slaveAddress, cancellationToken);
        RequireReady(current);
        if (current.Preset != preset)
        {
            await _bus.SelectPresetAsync(_slaveAddress, preset, cancellationToken);
            current = await _bus.ReadControllerStatusAsync(_slaveAddress, cancellationToken);
            RequireReady(current);
            RequirePreset(current, preset);
        }
        _requestedPreset = preset;
    }

    private void RequirePreset(AdcControllerStatus status, ushort preset)
    {
        if (status.Preset != preset)
            throw new InvalidOperationException(
                $"ADC {_slaveAddress} preset is {status.Preset}; this fastening requires preset {preset}.");
    }

    private void RequireDirection(AdcControllerStatus status, AdcDirection direction)
    {
        if (status.Direction != direction)
            throw new InvalidOperationException(
                $"ADC {_slaveAddress} direction is {status.Direction}; expected {direction}.");
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _bus.Open(_portName, _baudRate);
        await _bus.StopAsync(_slaveAddress, cancellationToken);
        var status = await WaitForStoppedAsync(cancellationToken);
        if (status.Alarm != 0)
        {
            await _bus.ResetAlarmAsync(_slaveAddress, cancellationToken);
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
            await _bus.SetDirectionAsync(_slaveAddress, AdcDirection.Loosening, cancellationToken);
            var ready = await _bus.ReadControllerStatusAsync(_slaveAddress, cancellationToken);
            RequireReady(ready);
            RequireDirection(ready, AdcDirection.Loosening);
            await _bus.StartAsync(_slaveAddress, cancellationToken);
            while (true)
            {
                var status = await _bus.ReadControllerStatusAsync(_slaveAddress, cancellationToken);
                if (status.Alarm != 0)
                    throw new InvalidOperationException(
                        $"ADC {_slaveAddress} controller error: {status.Alarm}.");
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

    public async Task<BoltResult> TightenAsync(
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? feedAsync = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HasPendingResult)
        {
            var result = await ReadPendingResultAsync(cancellationToken);
            if (result is not null)
                return result;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        AdcFasteningResult? completed = null;
        Exception? failure = null;
        try
        {
            var current = await _bus.ReadFasteningResultAsync(_slaveAddress, cancellationToken);
            if (current.Status == AdcEventStatus.Error)
            {
                completed = current;
            }
            else
            {
                var status = await _bus.ReadControllerStatusAsync(_slaveAddress, cancellationToken);
                var fastening = (EventCount: current.EventCount, Preset: _requestedPreset ?? status.Preset);
                RequireReady(status);
                RequirePreset(status, fastening.Preset);
                await _bus.SetDirectionAsync(_slaveAddress, AdcDirection.Fastening, cancellationToken);
                status = await _bus.ReadControllerStatusAsync(_slaveAddress, cancellationToken);
                RequireReady(status);
                RequirePreset(status, fastening.Preset);
                RequireDirection(status, AdcDirection.Fastening);

                timeout.CancelAfter(_connection.FasteningTimeoutMilliseconds);
                timeout.Token.ThrowIfCancellationRequested();
                _pendingFastening = fastening;
                // START may reach the controller even if its acknowledgement or feed fails.
                _feedUnconfirmed = feedAsync is not null;
                await _bus.StartAsync(_slaveAddress, timeout.Token);
                if (feedAsync is not null)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    await feedAsync(timeout.Token);
                    _feedUnconfirmed = false;
                }
                while (true)
                {
                    var result = await _bus.ReadFasteningResultAsync(_slaveAddress, timeout.Token);
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
                $"ADC {_slaveAddress} fastening timed out after {_connection.FasteningTimeoutMilliseconds} ms.");
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
        if (_feedUnconfirmed || _pendingFastening is not { } pending)
        {
            return null;
        }

        var result = await _bus.ReadFasteningResultAsync(_slaveAddress, cancellationToken);
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
        _feedUnconfirmed = false;
        _requestedPreset = null;
    }

    public async Task StopAsync()
    {
        // Once STOP is requested, finish the bounded RUN OFF check even if the caller cancels.
        await _bus.StopAsync(_slaveAddress, CancellationToken.None);
        await WaitForStoppedAsync(CancellationToken.None);
    }

    private async Task StopAfterOperationAsync(Exception? failure)
    {
        try
        {
            await StopAsync();
        }
        catch (Exception stopError) when (failure is not null)
        {
            throw new AggregateException("ADC operation and STOP both failed.", failure, stopError);
        }
    }

    private async Task<AdcControllerStatus> WaitForStoppedAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connection.ResponseTimeoutMilliseconds);
        try
        {
            while (true)
            {
                var status = await _bus.ReadControllerStatusAsync(_slaveAddress, timeout.Token);
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
                $"ADC {_slaveAddress} motor stop was not confirmed within {_connection.ResponseTimeoutMilliseconds} ms.",
                exception);
        }
    }

    private BoltResult Complete(AdcFasteningResult result)
    {
        _pendingFastening = null;
        _feedUnconfirmed = false;
        if (result.Status == AdcEventStatus.Error)
        {
            throw new InvalidOperationException($"ADC {_slaveAddress} controller error: {result.Error}.");
        }

        return new BoltResult(result.Status == AdcEventStatus.FasteningOk, result.Torque);
    }

    private bool IsCompleted(AdcFasteningResult result, (ushort EventCount, ushort Preset) pending)
    {
        switch (true)
        {
            case true when result.Status == AdcEventStatus.Error:
                return true;
            case true when result.EventCount == pending.EventCount
                || result.Status is not (AdcEventStatus.FasteningOk or AdcEventStatus.FasteningNg):
                return false;
        }
        if (result.Preset != pending.Preset
            || result.Direction != AdcDirection.Fastening)
            throw new InvalidOperationException(
                $"ADC {_slaveAddress} result event {result.EventCount} does not match this fastening: "
                + $"preset {result.Preset}, direction {result.Direction}; expected preset {pending.Preset}, Fastening.");
        return true;
    }
}
