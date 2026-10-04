using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using IBTM.Core;
using IBTM.Device;
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
        IAdcBus bus,
        IIoService io,
        FasteningHead head,
        HantasSettings connection,
        byte slaveAddress,
        string portName,
        int baudRate,
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
        _slaveAddress = slaveAddress;
        _portName = portName;
        _baudRate = baudRate;
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
        var responseTimeoutMilliseconds = _connection.ResponseTimeoutMilliseconds;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(responseTimeoutMilliseconds);
        // Wait for the shared acquisition loop, never issue a second status query here.
        var after = Stopwatch.GetTimestamp();
        _bus.Open(_portName, _baudRate);
        Monitor.IntervalMilliseconds = _connection.StatusPollMilliseconds;
        await Monitor.StartAsync(_slaveAddress, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(
            (long)responseTimeoutMilliseconds * _connection.ReadAttempts + Monitor.IntervalMilliseconds));
        var waitingForReady = false;
        try
        {
            var status = await Monitor.WaitForSampleAsync(after, timeout.Token);
            while (expectedPreset is { } preset && status is { Alarm: 0, Running: false }
                && (!status.Ready || status.Preset != preset)
                && !_io.GetOutput(_start))
            {
                if (!waitingForReady)
                    _logger.LogInformation(
                        "ADC {Port}/{Slave}: waiting for preset/READY; requested preset={RequestedPreset}, actual preset={ActualPreset}, READY={Ready}; START remains OFF.",
                        _portName, _slaveAddress, preset, status.Preset, status.Ready);
                waitingForReady = true;
                // READY may still describe the previous preset during an I/O changeover.
                // Require matching live feedback within the same overall deadline.
                status = await Monitor.WaitForSampleAsync(Stopwatch.GetTimestamp(), timeout.Token);
            }
            if (expectedPreset is not null)
                _logger.LogInformation("ADC {Port}/{Slave}: preset readiness checked; requested preset={RequestedPreset}, actual preset={ActualPreset}, READY={Ready}, RUN={Running}, ALARM={Alarm}, elapsed={ElapsedMs:F1} ms.",
                    _portName, _slaveAddress, expectedPreset, status.Preset, status.Ready, status.Running, status.Alarm,
                    Stopwatch.GetElapsedTime(after).TotalMilliseconds);
            return status;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (waitingForReady)
                throw new TimeoutException(
                    $"ADC {_portName}/{_slaveAddress}: READY timeout after preset selection; "
                    + $"requested preset={expectedPreset}, actual preset={Monitor.Sample?.Status?.Preset}, READY={Monitor.Sample?.Status?.Ready}; "
                    + $"last rejection={Monitor.Sample?.Rejection ?? "none"}.");
            throw new TimeoutException(
                $"ADC {_portName}/{_slaveAddress}: no fresh controller status from the monitor; "
                + $"last rejection={Monitor.Sample?.Rejection ?? "none"}.");
        }
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
        foreach (var output in _presets)
            _io.SetOutput(output, false);
        _io.SetOutput(_presets[preset - 1], true);
        _logger.LogInformation("ADC {Port}/{Slave}: preset {Preset} settling for {DelayMs} ms; START remains OFF.",
            _portName, _slaveAddress, preset, presetSettleMilliseconds);
        await Task.Delay(presetSettleMilliseconds, cancellationToken);
        // Check a fresh sample after the delay; elapsed time alone does not prove readiness.
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
        _bus.Open(_portName, _baudRate);
        Monitor.IntervalMilliseconds = _connection.StatusPollMilliseconds;
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
        ushort? torqueCompensationPercent = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(dryRunMilliseconds);
        var fasteningTimeoutMilliseconds = _connection.FasteningTimeoutMilliseconds;
        if (dryRunMilliseconds == 0)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fasteningTimeoutMilliseconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        (ushort EventCount, ushort Preset)? started = null;
        BoltResult? completed = null;
        AdcFasteningResult? lastResult = null;
        var runObserved = false;
        var startedAt = long.MaxValue;
        var stopped = new TaskCompletionSource<AdcStatusSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusSamples = 0;
        var rejectedSamples = 0;
        var longestQueryMilliseconds = 0.0;
        long? lastRunningQueryAt = null;
        AdcStatusSample? finishedSample = null;
        long? resultQueuedAt = null;
        long? resultReceivedAt = null;
        Exception? failure = null;
        var waitingForResult = false;
        Exception? ioFailure = null;
        void OnIoFaulted(Exception exception)
        {
            Interlocked.CompareExchange(ref ioFailure, exception, null);
            OperationCancellation.CancelIfNotDisposed(timeout);
        }
        void OnStatusSampled(AdcStatusSample sample)
        {
            var cycleStartedAt = Interlocked.Read(ref startedAt);
            if (sample.StartedAt < cycleStartedAt || stopped.Task.IsCompleted)
                return;
            var observedAt = Stopwatch.GetTimestamp();
            var queryMilliseconds = Stopwatch.GetElapsedTime(sample.StartedAt, sample.CompletedAt).TotalMilliseconds;
            statusSamples++;
            longestQueryMilliseconds = Math.Max(longestQueryMilliseconds, queryMilliseconds);
            // A rejected or unmatched valid reply supplies no RUN feedback or fastening result.
            // Await the monitor's next scheduled sample within the existing cycle timeout.
            if (sample.Rejection is not null)
            {
                rejectedSamples++;
                return;
            }
            if (sample.Error is { } error)
            {
                stopped.TrySetException(error);
                return;
            }
            if (sample.Status is not { } status)
                return;
            if (status.Running)
                lastRunningQueryAt = sample.StartedAt;
            if (status.Running && !runObserved)
            {
                runObserved = true;
                _logger.LogInformation(
                    "ADC {Port}/{Slave}: RUN ON observed; elapsed since START={Elapsed:F1} ms; "
                        + "status query={QueryElapsed:F1} ms; notification={Notification:F1} ms.",
                    _portName, _slaveAddress, Stopwatch.GetElapsedTime(cycleStartedAt, observedAt).TotalMilliseconds,
                    queryMilliseconds, Stopwatch.GetElapsedTime(sample.CompletedAt, observedAt).TotalMilliseconds);
            }
            if (status.Alarm != 0 || runObserved && !status.Running)
                stopped.TrySetResult(sample);
        }
        _io.Faulted += OnIoFaulted;
        try
        {
            _io.CheckReady();
            if (_io.GetOutput(_start))
                throw new InvalidOperationException("Turn START OFF before fastening.");
            _bus.Open(_portName, _baudRate);
            if (_requestedPreset is not { } preset)
                throw new InvalidOperationException("Select an I/O fastening preset before starting.");
            for (var index = 0; index < _presets.Length; index++)
            {
                if (_io.GetOutput(_presets[index]) != (index == preset - 1))
                    throw new InvalidOperationException($"Preset outputs no longer match preset {preset}.");
            }
            var initialEvent = dryRunMilliseconds > 0
                ? null : await Monitor.EnqueueAsync(
                    token => _bus.ReadRegistersAsync(_slaveAddress, AdcFunctionCode.ReadInputRegisters,
                        (ushort)AdcResultRegister.EventCount, 1, token), timeout.Token);
            // One pre-START baseline excludes previously received bolt results.
            var fastening = (EventCount: initialEvent?[0] ?? (ushort)0, Preset: preset);
            _io.SetOutput(_direction, false);

            if (dryRunMilliseconds == 0)
                timeout.CancelAfter(fasteningTimeoutMilliseconds);
            timeout.Token.ThrowIfCancellationRequested();
            started = fastening;
            if (dryRunMilliseconds == 0)
                Monitor.Sampled += OnStatusSampled;
            Interlocked.Exchange(ref startedAt, Stopwatch.GetTimestamp());
            // Own STOP cleanup before requesting START or lowering the head.
            _io.SetOutput(_start, true);
            if (feedAsync is not null && ioFailure is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await feedAsync(timeout.Token);
            }
            if (dryRunMilliseconds > 0)
                await Task.Delay(dryRunMilliseconds, timeout.Token);
            waitingForResult = dryRunMilliseconds == 0;
            if (dryRunMilliseconds == 0)
            {
                try
                {
                    finishedSample = await stopped.Task.WaitAsync(timeout.Token);
                    var finishedStatus = finishedSample.Status!;
                    Monitor.Sampled -= OnStatusSampled;
                    if (finishedStatus.Alarm != 0 && finishedStatus.Running)
                        failure = new InvalidOperationException(AdcControllerError.Describe(finishedStatus.Alarm));
                    if (failure is null)
                    {
                        _logger.LogInformation(
                            "ADC {Port}/{Slave}: RUN OFF; reading fastening result; start event={StartEvent}; "
                                + "elapsed since START={Elapsed:F1} ms; sample to result enqueue={Handoff:F1} ms.",
                            _portName, _slaveAddress, fastening.EventCount,
                            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                            Stopwatch.GetElapsedTime(finishedSample.CompletedAt).TotalMilliseconds);
                        resultQueuedAt = Stopwatch.GetTimestamp();
                        var result = await Monitor.EnqueueAsync(
                            token => _bus.ReadFasteningResultAsync(_slaveAddress, token), timeout.Token);
                        resultReceivedAt = Stopwatch.GetTimestamp();
                        lastResult = result;
                        var hasNewResult = result.EventCount != fastening.EventCount
                            && result.Status is AdcEventStatus.Error or AdcEventStatus.FasteningOk or AdcEventStatus.FasteningNg;
                        if (!hasNewResult)
                            failure = new InvalidOperationException(
                                $"RUN OFF, but no new completed fastening result: "
                                + $"start event={fastening.EventCount}, event={result.EventCount}, status={result.Status}, alarm={Monitor.Sample?.Status?.Alarm}.");
                        else if (result.Status != AdcEventStatus.Error
                            && (result.Preset != fastening.Preset || result.Direction != AdcDirection.Fastening))
                            throw new InvalidOperationException(
                                $"ADC {_slaveAddress} result event {result.EventCount} does not match this fastening: "
                                + $"preset {result.Preset}, direction {result.Direction}; expected preset {fastening.Preset}, Fastening.");
                        else
                        {
                            var error = result.Status == AdcEventStatus.Error || result.Error != 0
                                ? $"ADC {_portName}/{_slaveAddress} controller error: "
                                    + (result.Error == 0 ? "Error 이벤트 수신; 상세 오류 코드 없음."
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
                        }
                    }
                }
                catch (Exception exception) when (exception is AdcResponseException or AdcUnexpectedResponseException)
                {
                    failure = exception;
                }
                if (completed is not null)
                    resultReceived?.Invoke(completed);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && ioFailure is not null)
        {
            failure = ioFailure;
            throw failure;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            failure = new TimeoutException(
                $"ADC {_portName}/{_slaveAddress} fastening timed out after {fasteningTimeoutMilliseconds} ms; "
                + $"waiting for RUN ON then OFF / fastening result; RUN observed={runObserved}, last RUN={Monitor.Sample?.Status?.Running}; "
                + $"start event={started?.EventCount}, expected preset={started?.Preset}, "
                + $"last event={lastResult?.EventCount}, status={lastResult?.Status}, preset={lastResult?.Preset}, "
                + $"direction={lastResult?.Direction}, error={lastResult?.Error}; "
                + $"last status error={Monitor.Sample?.Error?.Message ?? Monitor.Sample?.Rejection ?? "none"}.");
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
            Monitor.Sampled -= OnStatusSampled;
            _io.Faulted -= OnIoFaulted;
            StopAfterOperation(failure);
            // Record the summary after START OFF; diagnostics must not hold the motor on.
            var stoppedAt = Stopwatch.GetTimestamp();
            if (finishedSample is { } sample)
            {
                _logger.LogInformation(
                    "ADC {Port}/{Slave}: completion status timing; start event={StartEvent}; "
                        + "RUN={Running}, ALARM={Alarm}; START to sample={Elapsed:F1} ms; "
                        + "samples={Samples}, rejected={Rejected}; longest query={LongestQuery:F1} ms; "
                        + "final query={QueryElapsed:F1} ms; last RUN query start to completion sample={RunGap:F1} ms; "
                        + "sample to result enqueue={Handoff:F1} ms.",
                    _portName, _slaveAddress, started?.EventCount, sample.Status?.Running, sample.Status?.Alarm,
                    Stopwatch.GetElapsedTime(startedAt, sample.CompletedAt).TotalMilliseconds,
                    statusSamples, rejectedSamples, longestQueryMilliseconds,
                    Stopwatch.GetElapsedTime(sample.StartedAt, sample.CompletedAt).TotalMilliseconds,
                    lastRunningQueryAt is { } runningAt
                        ? Stopwatch.GetElapsedTime(runningAt, sample.CompletedAt).TotalMilliseconds : (double?)null,
                    resultQueuedAt is { } queuedAt
                        ? Stopwatch.GetElapsedTime(sample.CompletedAt, queuedAt).TotalMilliseconds : (double?)null);
                if (completed?.Controller is { } controller
                    && resultReceivedAt is { } receivedAt
                    && resultQueuedAt is { } queued)
                {
                    _logger.LogInformation(
                        "ADC {Port}/{Slave}: cycle timing; result event={ResultEvent}; "
                            + "START to result={Elapsed:F1} ms; controller fastening={ControllerTime} ms; "
                            + "difference (includes mechanical/start time)={Difference:F1} ms; "
                            + "completion sample to result={CompletionToResult:F1} ms; result queue/read={ResultRead:F1} ms; "
                            + "result to STOP cleanup complete={Stop:F1} ms.",
                        _portName, _slaveAddress, controller.EventCount,
                        Stopwatch.GetElapsedTime(startedAt, receivedAt).TotalMilliseconds,
                        controller.FasteningTimeMilliseconds,
                        Stopwatch.GetElapsedTime(startedAt, receivedAt).TotalMilliseconds - controller.FasteningTimeMilliseconds,
                        Stopwatch.GetElapsedTime(sample.CompletedAt, receivedAt).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(queued, receivedAt).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(receivedAt, stoppedAt).TotalMilliseconds);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ioFailure is not null)
            throw ioFailure;
        if (completed is null && Monitor.Sample?.Status is { Alarm: > 0 } status)
            failure ??= new InvalidOperationException(AdcControllerError.Describe(status.Alarm));
        if (failure is not null)
        {
            // START is OFF. No torque was received for this bolt.
            return new BoltResult(false, null,
                Error: $"ADC {_portName}/{_slaveAddress}: {failure.Message}");
        }

        if (dryRunMilliseconds > 0)
            return new BoltResult(true, null, BoltResultSource.DryRun);

        if (completed is not null)
            return completed;

        throw new InvalidOperationException("ADC fastening ended without a result.");
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
