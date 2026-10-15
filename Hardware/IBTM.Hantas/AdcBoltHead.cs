using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IBTM.Hantas;

public sealed class AdcBoltHead : IBoltHead
{
    private readonly IAdcBus _bus;
    private readonly IIoService _io;
    private readonly OutputIo _start;
    private readonly OutputIo _direction;
    private readonly OutputIo _reset;
    private readonly OutputIo[] _presets;
    private const int ResetPulseMilliseconds = 100;
    private readonly HantasSettings _connection;
    private readonly byte _slaveAddress;
    private readonly ILogger<AdcBoltHead> _logger;
    // Connection edits apply to a newly created head, together with its slave address.
    private readonly string _portName;
    private readonly int _baudRate;
    // Requested preset; never a substitute for controller feedback.
    private ushort? _requestedPreset;

    public AdcBoltHead(
        [FromKeyedServices] IAdcBus bus,
        IIoService io,
        [ServiceKey] FasteningHead head,
        HantasSettings connection,
        byte? slaveAddress = null,
        string? portName = null,
        int? baudRate = null,
        ILogger<AdcBoltHead>? logger = null)
    {
        _bus = bus;
        _io = io;
        (_start, _direction, _reset) = head switch
        {
            FasteningHead.Pickup => (OutputIo.PickupBoltStart, OutputIo.PickupBoltDirection, OutputIo.PickupBoltReset),
            FasteningHead.Shooting => (OutputIo.ShootingBoltStart, OutputIo.ShootingBoltDirection, OutputIo.ShootingBoltReset),
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
        _presets = head == FasteningHead.Pickup
            ? [OutputIo.PickupBoltPreset1, OutputIo.PickupBoltPreset2, OutputIo.PickupBoltPreset3]
            : [OutputIo.ShootingBoltPreset1, OutputIo.ShootingBoltPreset2, OutputIo.ShootingBoltPreset3];
        _connection = connection;
        _slaveAddress = slaveAddress ?? (head == FasteningHead.Pickup
            ? connection.PickupSlaveAddress : connection.ShootingSlaveAddress);
        _portName = portName ?? (head == FasteningHead.Pickup
            ? connection.PickupPortName : connection.ShootingPortName);
        _baudRate = baudRate ?? (head == FasteningHead.Pickup
            ? connection.PickupBaudRate : connection.ShootingBaudRate);
        _logger = logger ?? NullLogger<AdcBoltHead>.Instance;
    }

    public AdcStatusMonitor Monitor => _bus.Monitor;

    public async Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.CheckReady();
        RequireReady(await WaitForStatusAsync(cancellationToken));
    }

    private void RequireReady(AdcControllerStatus status)
    {
        if (status.Alarm != 0 || !status.Ready || status.Running || _io.GetOutput(_start))
            throw new InvalidOperationException(
                $"ADC {_portName}/{_slaveAddress} not ready: "
                + $"{AdcControllerError.Describe(status.Alarm)}, READY={status.Ready}, "
                + $"RUN={status.Running}, START={_io.GetOutput(_start)}.");
    }

    private async Task<AdcControllerStatus> WaitForStatusAsync(
        CancellationToken cancellationToken, ushort? expectedPreset = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_connection.ResponseTimeoutMilliseconds);
        _bus.Open(_portName, _baudRate, _slaveAddress);
        await Monitor.StartAsync(_slaveAddress, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(
            (long)_connection.ResponseTimeoutMilliseconds * _connection.ReadAttempts));
        var status = await Monitor.ReadStatusAsync(timeout.Token);
        if (expectedPreset is { } preset && status.Preset != preset)
            throw new InvalidOperationException(
                $"ADC {_portName}/{_slaveAddress}: preset {status.Preset}; expected {preset}.");
        return status;
    }

    public async Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (preset is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(preset), "IO bolt presets are 1, 2 and 3.");
        _io.CheckReady();
        if (_io.GetOutput(_start))
            throw new InvalidOperationException("Turn START OFF before selecting a preset.");
        _requestedPreset = null;
        var presetSettleMilliseconds = _connection.PresetSettleMilliseconds;
        ArgumentOutOfRangeException.ThrowIfNegative(presetSettleMilliseconds);
        var presetOutputsMatch = true;
        for (var index = 0; index < _presets.Length; index++)
        {
            if (_io.GetOutput(_presets[index]) != (index == preset - 1))
            {
                presetOutputsMatch = false;
                break;
            }
        }
        if (!presetOutputsMatch)
        {
            foreach (var output in _presets)
                _io.SetOutput(output, false);
            _io.SetOutput(_presets[preset - 1], true);
            _logger.LogInformation("ADC {Port}/{Slave}: preset {Preset} settling for {DelayMs} ms; START remains OFF.",
                _portName, _slaveAddress, preset, presetSettleMilliseconds);
            await Task.Delay(presetSettleMilliseconds, cancellationToken);
        }
        // Matching outputs never replace fresh controller preset/READY feedback.
        var status = await WaitForStatusAsync(cancellationToken, expectedPreset: preset);
        if (status.Alarm != 0)
        {
            _logger.LogWarning("ADC {Port}/{Slave} reports alarm {Alarm}; resetting once before the next bolt.",
                _portName, _slaveAddress, status.Alarm);
            await ResetAsync(cancellationToken);
            status = await WaitForStatusAsync(cancellationToken, expectedPreset: preset);
        }
        RequireReady(status);
        cancellationToken.ThrowIfCancellationRequested();
        _requestedPreset = preset;
    }

    public async Task<ushort> ReadTorqueCompensationAsync(ushort preset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (preset is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(preset), "IO bolt presets are 1, 2 and 3.");
        _io.CheckReady();
        if (_io.GetOutput(_start))
            throw new InvalidOperationException("Turn START OFF before reading preset settings.");
        _bus.Open(_portName, _baudRate, _slaveAddress);
        await Monitor.StartAsync(_slaveAddress, cancellationToken);
        // ADC presets occupy 15 holding registers; the last is torque compensation (%).
        var registers = await Monitor.EnqueueAsync(
            token => _bus.ReadRegistersAsync(_slaveAddress, AdcFunctionCode.ReadHoldingRegisters,
                (ushort)(preset * 15), 1, token), cancellationToken);
        _logger.LogInformation("ADC {Port}/{Slave}: preset {Preset} torque compensation={Compensation}% at operation start.",
            _portName, _slaveAddress, preset, registers[0]);
        return registers[0];
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(_start, false);
        cancellationToken.ThrowIfCancellationRequested();
        Exception? failure = null;
        try
        {
            _io.SetOutput(_reset, true);
            await Task.Delay(ResetPulseMilliseconds, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                _io.SetOutput(_reset, false);
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException("ADC RESET and output release both failed.", failure, cleanupFailure);
            }
        }
        await CheckReadyAsync(cancellationToken);
    }

    // Manual hold-to-run only. Cancellation stops rotation; it is not a loose-complete result.
    public async Task RunReverseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.CheckReady();
        if (_io.GetOutput(_start))
            throw new InvalidOperationException("Turn START OFF before loosening.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? failure = null;
        Exception? ioFailure = null;
        void OnIoFaulted(Exception exception)
        {
            ioFailure = exception;
            OperationCancellation.CancelIfNotDisposed(operation);
        }
        _io.Faulted += OnIoFaulted;
        try
        {
            _io.SetOutput(_direction, true);
            await CheckReadyAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _io.SetOutput(_start, true);
            await Task.Delay(Timeout.Infinite, operation.Token);
        }
        catch (OperationCanceledException) when (ioFailure is not null)
        {
            failure = ioFailure;
            throw ioFailure;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _io.Faulted -= OnIoFaulted;
            StopAfterOperation(failure);
        }
    }

    public async Task<BoltResult> TightenAsync(
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? feedAsync = null,
        int dryRunMilliseconds = 0,
        Action<BoltResult>? resultReceived = null,
        ushort? torqueCompensationPercent = null,
        bool stopFeedOnCompletion = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(dryRunMilliseconds);
        var fasteningTimeoutMilliseconds = _connection.FasteningTimeoutMilliseconds;
        if (dryRunMilliseconds == 0 || feedAsync is not null)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fasteningTimeoutMilliseconds);
        var operationTimeoutMilliseconds = dryRunMilliseconds > 0
            ? feedAsync is null ? dryRunMilliseconds : Math.Min(dryRunMilliseconds, fasteningTimeoutMilliseconds)
            : fasteningTimeoutMilliseconds;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stopped = new TaskCompletionSource<AdcEventSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        ushort initialEvent = 0;
        ushort preset = 0;
        var startedAt = long.MaxValue;
        var runObserved = false;
        var feedCompleted = feedAsync is null;
        AdcEventSample? finished = null;
        long? feedStopRequestedAt = null;
        Exception? failure = null;
        Exception? ioFailure = null;
        BoltResult? completed = null;
        void OnFaulted(Exception exception)
        {
            Interlocked.CompareExchange(ref ioFailure, exception, null);
            OperationCancellation.CancelIfNotDisposed(timeout);
        }
        void OnStatusSampled(AdcStatusSample sample)
        {
            if (sample.Error is { } error)
                OnFaulted(error);
        }
        void OnEventReceived(AdcEventSample sample)
        {
            var result = sample.Result;
            if (sample.ReceivedAt < Interlocked.Read(ref startedAt) || result.EventCount == initialEvent
                || result.Status is not (AdcEventStatus.FasteningOk or AdcEventStatus.FasteningNg or AdcEventStatus.Error))
                return;
            if (result.Status != AdcEventStatus.Error
                && (result.Preset != preset || result.Direction != AdcDirection.Fastening))
            {
                stopped.TrySetException(new InvalidOperationException(
                    $"ADC {_portName}/{_slaveAddress} result event {result.EventCount}: "
                    + $"preset {result.Preset}, direction {result.Direction}; expected preset {preset}, Fastening."));
                return;
            }
            if (!stopped.TrySetResult(sample) && result.Status == AdcEventStatus.Error)
                OnFaulted(new InvalidOperationException(AdcControllerError.Describe(result.Error)));
        }
        _io.Faulted += OnFaulted;
        try
        {
            _io.CheckReady();
            if (_io.GetOutput(_start))
                throw new InvalidOperationException("Turn START OFF before fastening.");
            _bus.Open(_portName, _baudRate, _slaveAddress);
            if (_requestedPreset is not { } requestedPreset)
                throw new InvalidOperationException("Select an I/O fastening preset before starting.");
            preset = requestedPreset;
            for (var index = 0; index < _presets.Length; index++)
            {
                if (_io.GetOutput(_presets[index]) != (index == preset - 1))
                    throw new InvalidOperationException($"Preset outputs no longer match preset {preset}.");
            }
            await Monitor.StartAsync(_slaveAddress, timeout.Token);
            if (dryRunMilliseconds == 0)
            {
                var baseline = await Monitor.EnqueueAsync(
                    token => _bus.ReadRegistersAsync(_slaveAddress, AdcFunctionCode.ReadInputRegisters,
                        (ushort)AdcResultRegister.EventCount, 1, token), timeout.Token);
                initialEvent = baseline[0];
            }
            _io.SetOutput(_direction, false);
            Monitor.EventReceived += OnEventReceived;
            Monitor.Sampled += OnStatusSampled;
            if (Monitor.IsTorqueCurveMonitoringRequested)
                Monitor.BeginTorqueCurveCapture();
            timeout.CancelAfter(operationTimeoutMilliseconds);
            timeout.Token.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref startedAt, Stopwatch.GetTimestamp());
            _io.SetOutput(_start, true);
            if (feedAsync is not null)
            {
                // One fresh RUN check before Z feed; completion comes only from an event.
                var status = await Monitor.ReadStatusAsync(timeout.Token);
                runObserved = status.Running;
                if (!stopped.Task.IsCompleted)
                {
                    if (status.Alarm != 0 || !status.Running)
                        throw new InvalidOperationException(
                            $"ADC {_portName}/{_slaveAddress}: RUN not confirmed; RUN={status.Running}, "
                            + AdcControllerError.Describe(status.Alarm));
                    using var feedCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    var feeding = feedAsync(feedCancellation.Token);
                    if (await Task.WhenAny(feeding, stopped.Task) == stopped.Task
                        && (stopFeedOnCompletion || dryRunMilliseconds > 0
                            || !stopped.Task.IsCompletedSuccessfully
                            || (await stopped.Task).Result is { Error: > 0 } or { Status: AdcEventStatus.Error }))
                    {
                        feedStopRequestedAt = Stopwatch.GetTimestamp();
                        feedCancellation.Cancel();
                    }
                    try
                    {
                        await feeding;
                    }
                    catch (OperationCanceledException) when (feedCancellation.IsCancellationRequested
                        && !timeout.IsCancellationRequested && stopped.Task.IsCompleted)
                    {
                        // The result remains owned by this bolt after Z stops.
                    }
                }
            }
            feedCompleted = true;
            if (dryRunMilliseconds > 0)
            {
                timeout.CancelAfter(Timeout.Infinite);
                var remaining = TimeSpan.FromMilliseconds(dryRunMilliseconds) - Stopwatch.GetElapsedTime(startedAt);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, timeout.Token);
                if (stopped.Task.IsCompleted)
                {
                    finished = await stopped.Task;
                    failure = new InvalidOperationException(finished.Result.Error != 0
                        ? AdcControllerError.Describe(finished.Result.Error) : "Fastening completed during dry run.");
                }
            }
            else
            {
                finished = await stopped.Task.WaitAsync(timeout.Token);
                var result = finished.Result;
                var error = result.Status == AdcEventStatus.Error || result.Error != 0
                    ? $"ADC {_portName}/{_slaveAddress} controller error: "
                        + (result.Error == 0 ? "Error 이벤트 수신; 상세 오류 코드 없음"
                            : AdcControllerError.Describe(result.Error))
                        + $" event={result.EventCount}, status={result.Status}."
                    : null;
                completed = new BoltResult(result.Status == AdcEventStatus.FasteningOk && error is null,
                    result.Torque, Error: error)
                {
                    RecordedAt = DateTimeOffset.Now,
                    Controller = new(_portName, _slaveAddress, result.EventCount,
                        result.FasteningTimeMilliseconds, result.Preset, result.TargetTorque,
                        result.TargetSpeedRpm, result.Angle1, result.Angle2, result.Angle3,
                        result.ScrewCount, result.Error, (ushort)result.Direction, (ushort)result.Status,
                        result.SnugAngle, result.Registers)
                    {
                        TorqueCompensationPercent = result.Preset == preset ? torqueCompensationPercent : null,
                    },
                };
                resultReceived?.Invoke(completed);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && ioFailure is not null)
        {
            failure = ioFailure;
            throw failure;
        }
        catch (OperationCanceledException) when (dryRunMilliseconds > 0
            && operationTimeoutMilliseconds == dryRunMilliseconds
            && !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested
            && (feedAsync is null || runObserved))
        {
            if (stopped.Task.IsCompleted)
            {
                finished = await stopped.Task;
                failure = new InvalidOperationException(finished.Result.Error != 0
                    ? AdcControllerError.Describe(finished.Result.Error) : "Fastening completed during dry run.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            failure = new TimeoutException(
                $"ADC {_portName}/{_slaveAddress}: no fastening event within {operationTimeoutMilliseconds} ms; "
                + $"start event={initialEvent}, preset={preset}.");
            if (!feedCompleted)
                throw failure;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            Monitor.EventReceived -= OnEventReceived;
            Monitor.Sampled -= OnStatusSampled;
            _io.Faulted -= OnFaulted;
            StopAfterOperation(failure);
            if (finished is { } received)
                _logger.LogInformation(
                    "ADC {Port}/{Slave}: fastening event received; event={Event}, status={Status}, error={Error}; "
                        + "START to event={Elapsed:F1} ms; event to Z stop request={StopRequest:F1} ms.",
                    _portName, _slaveAddress, received.Result.EventCount, received.Result.Status, received.Result.Error,
                    Stopwatch.GetElapsedTime(startedAt, received.ReceivedAt).TotalMilliseconds,
                    feedStopRequestedAt is { } stopAt
                        ? Stopwatch.GetElapsedTime(received.ReceivedAt, stopAt).TotalMilliseconds : (double?)null);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (ioFailure is not null)
            throw ioFailure;
        if (failure is not null)
            return new BoltResult(false, null, Error: $"ADC {_portName}/{_slaveAddress}: {failure.Message}");
        if (dryRunMilliseconds > 0)
            return new BoltResult(true, null, BoltResultSource.DryRun);
        return completed ?? throw new InvalidOperationException("ADC fastening ended without a result.");
    }

    public void Stop()
    {
        // START is held while running; OFF stops the controller, including on timeout.
        _io.SetOutput(_start, false);
        _logger.LogInformation("ADC {Port}/{Slave}: I/O START OFF.", _portName, _slaveAddress);
    }

    private void StopAfterOperation(Exception? failure)
    {
        try
        {
            Stop();
        }
        catch (Exception stopError) when (failure is not null)
        {
            throw new AggregateException("ADC operation and STOP cleanup both failed.", failure, stopError);
        }
    }
}
