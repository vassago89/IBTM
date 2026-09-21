using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IBTM.Hantas;

public sealed class AdcBoltHead : IBoltHead
{
    private readonly IAdcBus _bus;
    private readonly HantasSettings _connection;
    private readonly byte _slaveAddress;
    private readonly ILogger<AdcBoltHead> _logger;
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
        int baudRate,
        ILogger<AdcBoltHead>? logger = null)
    {
        _bus = bus;
        _connection = connection;
        _slaveAddress = slaveAddress;
        _portName = portName;
        _baudRate = baudRate;
        _logger = logger ?? NullLogger<AdcBoltHead>.Instance;
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
            throw new InvalidOperationException(
                $"ADC {_portName}/{_slaveAddress} controller error: {AdcControllerError.Describe(status.Alarm)} "
                + $"READY={status.Ready}, RUN={status.Running}, Preset={status.Preset}. 다음 START 불가.");
        if (!status.Ready || status.Running)
            throw new InvalidOperationException(
                $"ADC {_slaveAddress} must be ready and stopped before starting.");
    }

    public async Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
    {
        var current = await _bus.ReadControllerStatusAsync(_slaveAddress, cancellationToken);
        if (current.Alarm != 0)
        {
            _logger.LogWarning(
                "ADC {Port}/{Slave} alarm before next bolt: {Error}. Resetting once before preset selection.",
                _portName, _slaveAddress, AdcControllerError.Describe(current.Alarm));
            await ResetAsync(cancellationToken);
            current = await _bus.ReadControllerStatusAsync(_slaveAddress, cancellationToken);
        }
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connection.ResponseTimeoutMilliseconds);
        try
        {
            do
            {
                status = await _bus.ReadControllerStatusAsync(_slaveAddress, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (status.Alarm == 0 && status.Ready && !status.Running)
                    break;
                await Task.Delay(StatusPollMilliseconds, timeout.Token);
            } while (true);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"ADC {_portName}/{_slaveAddress} RESET 후 준비 상태가 확인되지 않았습니다 "
                + $"({_connection.ResponseTimeoutMilliseconds} ms). "
                + $"{AdcControllerError.Describe(status.Alarm)} "
                + $"READY={status.Ready}, RUN={status.Running}, Preset={status.Preset}. 다음 START 불가.",
                exception);
        }
        _logger.LogInformation(
            "ADC {Port}/{Slave} RESET confirmed: Alarm={Alarm}, Ready={Ready}, RUN={Running}.",
            _portName, _slaveAddress, status.Alarm, status.Ready, status.Running);
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
                        $"ADC {_portName}/{_slaveAddress} controller error: {AdcControllerError.Describe(status.Alarm)}");
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
        Func<CancellationToken, Task>? feedAsync = null,
        int dryRunMilliseconds = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(dryRunMilliseconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        (ushort EventCount, ushort Preset)? started = null;
        AdcFasteningResult? completed = null;
        AdcFasteningResult? lastResult = null;
        Exception? failure = null;
        var waitingForResult = false;
        try
        {
            var current = dryRunMilliseconds > 0
                ? null : await _bus.ReadFasteningResultAsync(_slaveAddress, cancellationToken);
            lastResult = current;
            // The pre-START event is a baseline, including a previous bolt's error.
            var status = await _bus.ReadControllerStatusAsync(_slaveAddress, cancellationToken);
            var fastening = (EventCount: current?.EventCount ?? (ushort)0, Preset: _requestedPreset ?? status.Preset);
            RequireReady(status);
            RequirePreset(status, fastening.Preset);
            await _bus.SetDirectionAsync(_slaveAddress, AdcDirection.Fastening, cancellationToken);
            status = await _bus.ReadControllerStatusAsync(_slaveAddress, cancellationToken);
            RequireReady(status);
            RequirePreset(status, fastening.Preset);
            RequireDirection(status, AdcDirection.Fastening);

            if (dryRunMilliseconds == 0)
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
            if (dryRunMilliseconds > 0)
                await Task.Delay(dryRunMilliseconds, cancellationToken);
            waitingForResult = dryRunMilliseconds == 0;
            while (dryRunMilliseconds == 0)
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
            if (!waitingForResult)
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

        cancellationToken.ThrowIfCancellationRequested();
        if (dryRunMilliseconds > 0)
            return new BoltResult(true, null, BoltResultSource.DryRun);

        if (failure is not null)
        {
            // STOP and RUN OFF have completed. No torque was received for this bolt.
            return new BoltResult(false, null,
                Error: $"ADC {_portName}/{_slaveAddress}: {failure.Message}");
        }

        if (completed is not null)
        {
            var error = completed.Status == AdcEventStatus.Error || completed.Error != 0
                ? $"ADC {_portName}/{_slaveAddress} controller error: "
                    + (completed.Error == 0 ? "Error 이벤트 수신; 상세 오류 코드 없음."
                        : AdcControllerError.Describe(completed.Error))
                    + $" event={completed.EventCount}, status={completed.Status}."
                : null;
            return new BoltResult(completed.Status == AdcEventStatus.FasteningOk && error is null, completed.Torque,
                Error: error)
            {
                RecordedAt = DateTimeOffset.Now,
                Controller = new(_portName, _slaveAddress, completed.EventCount,
                    completed.FasteningTimeMilliseconds, completed.Preset, completed.TargetTorque,
                    completed.TargetSpeedRpm, completed.Angle1, completed.Angle2, completed.Angle3,
                    completed.ScrewCount, completed.Error, (ushort)completed.Direction, (ushort)completed.Status,
                    completed.SnugAngle, completed.Registers),
            };
        }

        throw new InvalidOperationException("ADC fastening ended without a result.");
    }

    public async Task StopAsync()
    {
        // Once STOP is requested, finish the bounded RUN OFF check even if the caller cancels.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await _bus.StopAsync(_slaveAddress, CancellationToken.None);
                break;
            }
            catch (AdcResponseException exception) when (exception.ErrorCode == 0x03)
            {
                _logger.LogWarning(exception,
                    "ADC {Port}/{Slave} STOP attempt {Attempt} was rejected; checking current RUN feedback.",
                    _portName, _slaveAddress, attempt + 1);
                var status = await _bus.ReadControllerStatusAsync(_slaveAddress, CancellationToken.None);
                _logger.LogWarning(
                    "ADC {Port}/{Slave} feedback after STOP rejection: RUN={Running}, Ready={Ready}, Alarm={Alarm}, Preset={Preset}, Direction={Direction}.",
                    _portName, _slaveAddress, status.Running, status.Ready, status.Alarm, status.Preset, status.Direction);
                if (!status.Running)
                {
                    _logger.LogWarning(
                        "ADC {Port}/{Slave} STOP was rejected but current RUN is OFF; motor stop confirmed. Alarm={Alarm}.",
                        _portName, _slaveAddress, status.Alarm);
                    return;
                }
                if (attempt == 1)
                    throw;
                // STOP is idempotent. A rejected START must never take this retry path.
                _logger.LogWarning(
                    "ADC {Port}/{Slave} RUN is still ON; sending STOP once more.", _portName, _slaveAddress);
            }
        }
        var stopped = await WaitForStoppedAsync(CancellationToken.None);
        _logger.LogInformation(
            "ADC {Port}/{Slave} STOP confirmed: RUN={Running}, Ready={Ready}, Alarm={Alarm}.",
            _portName, _slaveAddress, stopped.Running, stopped.Ready, stopped.Alarm);
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
