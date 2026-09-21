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
    private const int StatusPollMilliseconds = 50;
    // Connection edits apply to a newly created head, together with its slave address.
    private readonly string _portName;
    private readonly int _baudRate;
    // Requested preset; never a substitute for controller feedback.
    private ushort? _requestedPreset;

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
                await Task.Delay(StatusPollMilliseconds, cancellationToken);
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        (ushort EventCount, ushort Preset)? started = null;
        AdcFasteningResult? completed = null;
        AdcFasteningResult? lastResult = null;
        AdcResponseException? responseError = null;
        Exception? failure = null;
        try
        {
            var current = await _bus.ReadFasteningResultAsync(_slaveAddress, cancellationToken);
            lastResult = current;
            // The pre-START event is a baseline, including a previous bolt's error.
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
            started = fastening;
            // START may reach the controller even if its acknowledgement or feed fails.
            await _bus.StartAsync(_slaveAddress, timeout.Token);
            if (feedAsync is not null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await feedAsync(timeout.Token);
            }
            while (true)
            {
                try
                {
                    var result = await _bus.ReceiveFasteningResultAsync(_slaveAddress, timeout.Token);
                    lastResult = result;
                    if (IsCompleted(result, fastening))
                    {
                        completed = result;
                        break;
                    }
                }
                catch (AdcResponseException exception)
                {
                    responseError = exception;
                    failure = exception;
                    break;
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            failure = exception;
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            failure = new TimeoutException(
                $"ADC {_portName}/{_slaveAddress} fastening timed out after {_connection.FasteningTimeoutMilliseconds} ms; "
                + "waiting for Auto Data Output (check controller output enable/port); "
                + $"start event={started?.EventCount}, expected preset={started?.Preset}, "
                + $"last event={lastResult?.EventCount}, status={lastResult?.Status}, preset={lastResult?.Preset}, "
                + $"direction={lastResult?.Direction}, error={lastResult?.Error}.");
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

        if (responseError is not null)
        {
            // STOP and RUN OFF have completed. No torque was received for this bolt.
            return new BoltResult(false, null,
                Error: $"ADC {_portName}/{_slaveAddress}: {responseError.Message}");
        }

        if (completed is not null)
        {
            var error = completed.Status == AdcEventStatus.Error || completed.Error != 0
                ? $"ADC {_portName}/{_slaveAddress} controller error: {completed.Error}; event={completed.EventCount}, status={completed.Status}."
                : null;
            return new BoltResult(completed.Status == AdcEventStatus.FasteningOk && error is null, completed.Torque,
                Error: error);
        }

        throw new InvalidOperationException("ADC fastening ended without a result.");
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
                await Task.Delay(StatusPollMilliseconds, timeout.Token);
            }
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"ADC {_portName}/{_slaveAddress} motor stop was not confirmed within {_connection.ResponseTimeoutMilliseconds} ms (waiting for RUN OFF).",
                exception);
        }
    }

    private bool IsCompleted(AdcFasteningResult result, (ushort EventCount, ushort Preset) pending)
    {
        switch (true)
        {
            case true when result.EventCount == pending.EventCount:
                return false;
            case true when result.Status == AdcEventStatus.Error:
                return true;
            case true when result.Status is not (AdcEventStatus.FasteningOk or AdcEventStatus.FasteningNg):
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
